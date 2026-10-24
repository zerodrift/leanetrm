// What the Blotter desk shows: which backend groups and rows each tile, chart, group header and position row
// presents, and in which words. It selects, orders and labels exact server values; it never adds, nets, converts
// or infers them (AGENTS.md: TypeScript is presentation only).
import {compareDecimalStrings,type PivotGroup} from './analytics-model.js';
import type {DisplayPrecision} from './amount-display.js';
import type {RowGroup} from './blotter-layout.js';
import {palette,type ChartPoint,type ChartSpec} from './chart-view.js';
import {curveName,deliveryLabel} from './market-view.js';
import type {ShapePoint} from './mini-charts.js';
import type {Booking} from './trade-annotations.js';
import {excludedPositions,valuationNote,valuationReason,type ValuationReason} from './valuation-status.js';

export type Reference={revision:{catalogId:string;revisionId:string};code:string};
/** A position as the trading workspace reads it. */
export type Holding={booking:Booking;commodity:{code:string;name:string};priceTick?:string;markTick?:string;fixedAmountFormat:DisplayPrecision;quoteMultiplier?:string;amountFormat:DisplayPrecision;
  instrumentType:string;traderId:string|null;trader:string|null;traderCode:string|null;memo:string|null;tradeReference:string;instrumentReference:string;tradeId:string;bindingId:string;
  bookId:string;book:string;portfolioId:string;portfolio:string;contractId:string;revision:string;curve:string;definitionRevisionId:string;deliveryFrom:string;deliveryTo:string;
  lots:string|null;quantity:string;unit:string;quantitySpecification:Reference;executedAt:string;executionPrice:string|null;fixedPrice:string|null;mark:string|null;markMinusPrice:string|null;
  executionPnl:string|null;presentValue:string|null;currency:string;currencyReference:Reference;settlement:string;direction:'pay_fixed'|'receive_fixed'|'multi_side'|null;unavailableReason:ValuationReason|null};
export type Summary={view:{groupBy:string[];measures:string[];pivotConvention:string|null};groups:PivotGroup[]};

const moneyTotals='trading_blotter_money_totals_v1',quantityTotals='trading_blotter_totals_v1';
const moneyMeasures=['presentValue','executionPnl','tradeCount'];
// The desk's groupings of its one read: tile totals and splits as money per currency (D216), the position mix
// and the markets per quantity unit (D98).
const desk=[
  {groupBy:['instrumentType'],measures:moneyMeasures,pivotConvention:moneyTotals},
  {groupBy:['book'],measures:moneyMeasures,pivotConvention:moneyTotals},
  {groupBy:['commodity','instrumentType'],measures:['tradeCount','quantity'],pivotConvention:quantityTotals},
  {groupBy:['commodity','market'],measures:['presentValue','executionPnl','quantity','tradeCount'],pivotConvention:quantityTotals},
] as const;
type DeskSummary=0|1|2|3;
const byType=0,byBook=1,mix=2,byMarket=3;

/** The summaries the desk asks the workspace read for, in order, over the books and portfolios of that read. */
export function deskSummaryViews(bookIds:readonly string[],portfolioIds:readonly string[]){
  return desk.map(s=>({mode:'pivot' as const,bookIds:[...bookIds],portfolioIds:[...portfolioIds],groupBy:[...s.groupBy],measures:[...s.measures],pivotConvention:s.pivotConvention,priceNumericalPolicy:null}));
}

const missingSummaries='The trading workspace did not return the desk summaries. Refresh and try again.';
const inconsistent='The trading workspace returned an inconsistent desk summary. Refresh and try again.';
function groupsOf(summaries:readonly Summary[],which:DeskSummary):PivotGroup[]{
  const wanted=JSON.stringify([desk[which].groupBy,desk[which].measures,desk[which].pivotConvention]);
  const found=summaries.filter(s=>JSON.stringify([s.view?.groupBy,s.view?.measures,s.view?.pivotConvention])===wanted);
  if(found.length!==1||!Array.isArray(found[0].groups))throw Error(missingSummaries);
  return found[0].groups;
}
function text(group:PivotGroup,key:string):string{const value=group[key];if(typeof value!=='string'||!value)throw Error(inconsistent);return value;}
function amount(group:PivotGroup,key:string):string|null{
  const value=group[key];if(value===null)return null;
  if(typeof value!=='string'||!/^-?\d+(?:\.\d+|\/[1-9]\d*)?$/.test(value))throw Error(inconsistent);
  return value;
}
function count(group:PivotGroup,key:string):number{const value=group[key];if(typeof value!=='number'||!Number.isInteger(value)||value<0)throw Error(inconsistent);return value;}
function referenceKey(value:unknown):string{
  const reference=value as Reference|undefined;
  if(typeof reference?.code!=='string'||typeof reference.revision?.catalogId!=='string'||typeof reference.revision.revisionId!=='string')throw Error(inconsistent);
  return JSON.stringify([reference.revision.catalogId,reference.revision.revisionId,reference.code]);
}
const commodityOf=(group:PivotGroup)=>{const named=group.commodity as {code?:unknown;name?:unknown}|undefined;if(typeof named?.code!=='string'||typeof named.name!=='string')throw Error(inconsistent);return named as {code:string;name:string};};
const byText=(a:string,b:string)=>a<b?-1:a>b?1:0;

// The measure, labels and pill of each instrument type the Blotter holds.
const instruments:Readonly<Record<string,{measure:'presentValue'|'executionPnl';label:'PV'|'P&L';pill:'FUT'|'SWAP';plural:string;price:'executionPrice'|'fixedPrice';priceLabel:'Executed'|'Fixed'}>>={
  future:{measure:'executionPnl',label:'P&L',pill:'FUT',plural:'Futures',price:'executionPrice',priceLabel:'Executed'},
  financial_swap:{measure:'presentValue',label:'PV',pill:'SWAP',plural:'Swaps',price:'fixedPrice',priceLabel:'Fixed'},
};
function instrument(type:unknown){if(typeof type!=='string'||!Object.hasOwn(instruments,type))throw Error('The Blotter returned an unknown instrument type.');return instruments[type];}

/** Distinct labels for distinct keys: a label several keys share is followed by each key's place among them. */
function distinct(labels:ReadonlyMap<string,string>,word:string):Map<string,string>{
  const shared=new Map<string,string[]>();
  for(const [key,label] of labels)shared.set(label,[...(shared.get(label)??[]),key]);
  return new Map([...labels].map(([key,label])=>{const keys=shared.get(label)!.sort(byText);return [key,keys.length>1?`${label} · ${word} ${keys.indexOf(key)+1}`:label];}));
}
const currencyLabels=(groups:readonly PivotGroup[])=>distinct(new Map(groups.map(g=>[referenceKey(g.currencyReference),text(g,'currency')])),'reference');
const marketKey=(market:string,revision:string)=>`${market}|${revision}`;
// A market is identified by its code and definition revision; only names that two such markets share say which.
const marketLabels=(groups:readonly PivotGroup[])=>distinct(new Map(groups.map(g=>[marketKey(text(g,'market'),text(g,'definitionRevisionId')),curveName(text(g,'market'))])),'definition');

/** What a total leaves out: nothing, some positions, or every position, when it has no value at all. */
function totalNote(total:string|null,excluded:number):string|null{
  if(total!==null)return valuationNote(excluded);
  if(!excluded)throw Error('The Blotter returned a total without a value or a reason.');
  return `No valued positions · ${excluded} excluded`;
}

export type TileRow={currencyKey:string;currency:string;total:string|null;trades:number;excluded:number;note:string|null;byBook:{bookId:string;book:string;value:string|null;excluded:number}[]};
/** A money tile: one row per exact currency, never added across currencies. `note` is the tile's own when it has one row. */
export type Tile={key:'swapPv'|'futuresPnl';rows:TileRow[];note:string|null};
/** Swap PV and futures execution P&L, each per currency and split by book, from the money totals (D216). */
export function summaryTiles(summaries:readonly Summary[]):Tile[]{
  const types=groupsOf(summaries,byType),books=groupsOf(summaries,byBook);
  const labels=currencyLabels([...types,...books]);
  return (['swapPv','futuresPnl'] as const).map(key=>{
    const type=key==='swapPv'?'financial_swap':'future',{measure,plural}=instrument(type);
    const own=types.filter(g=>instrument(g.instrumentType)===instrument(type));
    const rows=own.map(group=>{
      const currencyKey=referenceKey(group.currencyReference),total=amount(group,measure),excluded=excludedPositions(group.unvaluedPositions);
      const byBookRows=books.filter(b=>instrument(b.instrumentType)===instrument(type)&&referenceKey(b.currencyReference)===currencyKey)
        .map(b=>({bookId:text(b,'bookId'),book:text(b,'book'),value:amount(b,measure),excluded:excludedPositions(b.unvaluedPositions)}))
        .sort((a,b)=>a.book.localeCompare(b.book)||byText(a.bookId,b.bookId));
      if(new Set(byBookRows.map(b=>b.bookId)).size!==byBookRows.length)throw Error(inconsistent);
      return {currencyKey,currency:labels.get(currencyKey)!,total,trades:count(group,'tradeCount'),excluded,note:totalNote(total,excluded),byBook:byBookRows};
    }).sort((a,b)=>a.currency.localeCompare(b.currency)||byText(a.currencyKey,b.currencyKey));
    if(new Set(rows.map(r=>r.currencyKey)).size!==rows.length)throw Error(inconsistent);
    return {key,rows,note:!rows.length?`No ${plural.toLowerCase()} in the selected books`:rows.length===1?rows[0].note:null};
  });
}

export type MixSegment={key:string;commodity:string;commodityName:string;instrumentType:string;label:string;trades:number};
export type PositionsTile={count:number;segments:MixSegment[];note:string|null};
/**
 * The positions the read returned (the number of its rows), mixed by the backend's commodity and instrument type groups,
 * one segment per group, never summed per commodity. `unvalued` is the read's own count of positions it cannot value.
 */
export function positionsTile(summaries:readonly Summary[],rows:number,unvalued:number):PositionsTile{
  const groups=groupsOf(summaries,mix);
  const entries=groups.map(g=>{const commodity=commodityOf(g),type=text(g,'instrumentType');
    return {key:JSON.stringify([commodity.code,type,referenceKey(g.quantitySpecification),referenceKey(g.currencyReference)]),commodity:commodity.code,commodityName:commodity.name,instrumentType:type,
      base:`${commodity.name} · ${instrument(type).plural}`,unit:text(g,'unit'),currency:text(g,'currency'),trades:count(g,'tradeCount')};});
  // Segments of one commodity and instrument type say what keeps them apart: their unit, their currency, or both.
  const label=(e:typeof entries[number])=>{const same=entries.filter(o=>o.base===e.base);if(same.length===1)return e.base;
    return [e.base,...(new Set(same.map(o=>o.unit)).size>1?[e.unit]:[]),...(new Set(same.map(o=>o.currency)).size>1?[e.currency]:[])].join(' · ');};
  const labels=distinct(new Map(entries.map(e=>[e.key,label(e)])),'group');
  const segments=entries.map(({key,commodity,commodityName,instrumentType,trades})=>({key,commodity,commodityName,instrumentType,label:labels.get(key)!,trades}))
    .sort((a,b)=>a.label.localeCompare(b.label)||byText(a.key,b.key));
  if(!Number.isInteger(rows)||rows<0)throw Error(inconsistent);
  const excluded=excludedPositions(unvalued);
  return {count:rows,segments,note:excluded?`Includes ${excluded} position${excluded===1?'':'s'} not valued at this close`:null};
}

export type Subtotal={measure:'PV'|'P&L';amount:string|null;currency:string;excluded:number;note:string|null};
export type GroupHeader={key:string;label:string;subtotals:Subtotal[]};
/**
 * How the rows group under `rowGroup`: each holding's group key, and each group's header with the backend subtotals of
 * the matching summary (money totals for books and instrument types, the market summary for markets). Null is a flat list.
 */
export function deskGroups(rowGroup:RowGroup,summaries:readonly Summary[],holdings:readonly Holding[]):{keys:string[];headers:GroupHeader[]}|null{
  if(rowGroup===null)return null;
  const groups=groupsOf(summaries,rowGroup==='market'?byMarket:rowGroup==='book'?byBook:byType);
  const currencies=currencyLabels(groups),markets=rowGroup==='market'?marketLabels(groups):undefined;
  const keyOf=(g:PivotGroup)=>rowGroup==='book'?text(g,'bookId'):rowGroup==='market'?marketKey(text(g,'market'),text(g,'definitionRevisionId')):text(g,'instrumentType');
  const labelOf=(g:PivotGroup)=>rowGroup==='book'?text(g,'book'):rowGroup==='market'?markets!.get(keyOf(g))!:instrument(g.instrumentType).plural;
  const headers=new Map<string,GroupHeader>();
  for(const group of groups){
    const key=keyOf(group),{measure,label}=instrument(group.instrumentType),total=amount(group,measure),excluded=excludedPositions(group.unvaluedPositions);
    const header=headers.get(key)??{key,label:labelOf(group),subtotals:[]};
    header.subtotals.push({measure:label,amount:total,currency:currencies.get(referenceKey(group.currencyReference))!,excluded,note:totalNote(total,excluded)});headers.set(key,header);
  }
  const keys=holdings.map(h=>rowGroup==='book'?h.bookId:rowGroup==='market'?marketKey(h.curve,h.definitionRevisionId):h.instrumentType);
  if(keys.some(key=>!headers.has(key)))throw Error(missingSummaries);
  const order=['PV','P&L'];
  return {keys,headers:[...headers.values()].filter(h=>keys.includes(h.key))
    .map(h=>({...h,subtotals:h.subtotals.sort((a,b)=>order.indexOf(a.measure)-order.indexOf(b.measure)||a.currency.localeCompare(b.currency))}))
    .sort((a,b)=>a.label.localeCompare(b.label)||byText(a.key,b.key))};
}

const zero=(value:string)=>/^-?0+(?:\.0+)?(?:\/\d+)?$/.test(value);
/** A display amount with its sign always shown: + for a gain, − for a loss, none for zero. */
export function signedText(value:string,format:(value:string)=>string):string{
  const shown=format(value);
  return zero(value)?shown:value.startsWith('-')?'−'+shown.replace(/^-/,''):'+'+shown;
}

/**
 * Swap PV by market: one horizontal panel per currency, gains right and losses left, largest gain first, each market
 * marked with its commodity's color. The panel says what it leaves out with its currency's own backend count.
 */
export function pvChartPanels(summaries:readonly Summary[],colors:ReadonlyMap<string,string>,format:(value:string)=>string):ChartSpec[]{
  const markets=groupsOf(summaries,byMarket),totals=groupsOf(summaries,byType),labels=marketLabels(markets);
  const swaps=markets.filter(g=>instrument(g.instrumentType)===instruments.financial_swap);
  const currencies=currencyLabels(swaps),panels=new Map<string,ChartPoint[]>();
  for(const group of swaps){
    const key=marketKey(text(group,'market'),text(group,'definitionRevisionId')),value=amount(group,'presentValue'),currency=referenceKey(group.currencyReference);
    panels.set(currency,[...(panels.get(currency)??[]),{x:key,label:labels.get(key)!,y:value,marker:colors.get(commodityOf(group).code),...(value===null?{}:{text:signedText(value,format)})}]);
  }
  return [...panels].map(([currency,points])=>{
    const total=totals.filter(g=>instrument(g.instrumentType)===instruments.financial_swap&&referenceKey(g.currencyReference)===currency);
    if(total.length!==1)throw Error(missingSummaries);
    const note=totalNote(amount(total[0],'presentValue'),excludedPositions(total[0].unvaluedPositions)),unit=currencies.get(currency)!;
    points.sort((a,b)=>a.y===null?(b.y===null?a.label!.localeCompare(b.label!):1):b.y===null?-1:compareDecimalStrings(b.y,a.y)||a.label!.localeCompare(b.label!));
    return {unit,spec:{title:'Swap PV by market',subtitle:[unit,'bar right = gain',...(note?[note]:[])].join(' · '),axis:'category',categoryTitle:'Market',unit,kind:'bar',orientation:'horizontal',colorBySign:true,
      series:[{name:'Swap PV',points}]} satisfies ChartSpec};
  }).sort((a,b)=>a.unit.localeCompare(b.unit)).map(p=>p.spec);
}

/**
 * Long and short by market: one horizontal panel per quantity unit, each bar in its commodity's color. A future's backend
 * quantity is signed. A swap's is its unsigned booked quantity, so its bar takes the side the backend's direction states
 * (pay fixed long, receive fixed short) where every swap of that market and unit shares one; otherwise it is not drawn.
 */
export function positionChartPanels(summaries:readonly Summary[],holdings:readonly Holding[],colors:ReadonlyMap<string,string>,format:(value:string)=>string):ChartSpec[]{
  const markets=groupsOf(summaries,byMarket),labels=marketLabels(markets),units=distinct(new Map(markets.map(g=>[referenceKey(g.quantitySpecification),text(g,'unit')])),'quantity');
  const panels=new Map<string,ChartPoint[]>();
  for(const group of markets){
    const market=marketKey(text(group,'market'),text(group,'definitionRevisionId')),type=instrument(group.instrumentType),unit=referenceKey(group.quantitySpecification),currency=referenceKey(group.currencyReference);
    const quantity=amount(group,'quantity');if(quantity===null)throw Error(inconsistent);
    const point:ChartPoint={x:`${market}|${type.pill}`,label:`${labels.get(market)} · ${type.plural}`,y:quantity,color:colors.get(commodityOf(group).code)};
    if(type===instruments.future)point.text=signedText(quantity,format);
    else{
      const directions=new Set(holdings.filter(h=>instrument(h.instrumentType)===type&&marketKey(h.curve,h.definitionRevisionId)===market&&referenceKey(h.quantitySpecification)===unit&&referenceKey(h.currencyReference)===currency).map(h=>h.direction));
      if(!directions.size)throw Error(missingSummaries);
      const [only]=directions;
      if(directions.size===1&&(only==='pay_fixed'||only==='receive_fixed')){point.direction=only==='pay_fixed'?'positive':'negative';point.text=`${format(quantity)} ${only==='pay_fixed'?'pay':'receive'} fixed`;}
      else{point.y=null;point.label+=directions.size===1&&only==='multi_side'?' · multi-side':' · mixed directions';}
    }
    panels.set(unit,[...(panels.get(unit)??[]),point]);
  }
  return [...panels].map(([unit,points])=>({unit:units.get(unit)!,points:points.sort((a,b)=>a.label!.localeCompare(b.label!))}))
    .sort((a,b)=>a.unit.localeCompare(b.unit))
    .map(({unit,points})=>({title:'Long and short by market',subtitle:`${unit} · bar right = long`,axis:'category',categoryTitle:'Market',unit,kind:'bar',orientation:'horizontal',series:[{name:'Quantity',points}]}));
}

// The design's commodity identity colors (blotter-desk.md, Visual system), for the commodities of the synthetic reference catalog.
const identity:Readonly<Record<string,string>>={test_gas:palette[2],test_power:palette[1]};
/** A color for each commodity: its identity color, or the next unused categorical color in code order. Marks always name it too. */
export function commodityColors(codes:Iterable<string>):Map<string,string>{
  const free=palette.filter(color=>!Object.values(identity).includes(color));let next=0;
  return new Map([...new Set(codes)].sort(byText).map(code=>[code,Object.hasOwn(identity,code)?identity[code]:free[next++%free.length]]));
}

export type DeskRow={tradeId:string;revision:string;instrumentType:string;pill:'FUT'|'SWAP';reference:string;
  market:string;commodity:string;commodityName:string;curve:string;delivery:string;deliveryFrom:string;deliveryTo:string;
  quantity:string;unit:string;quantityKey:string;side:string;exposure:'long'|'short'|'flat'|null;
  price:string|null;priceLabel:'Executed'|'Fixed';mark:string|null;value:string|null;measure:'P&L'|'PV';currency:string;unavailable:string|null;
  /** The words its cells draw beside their exact amounts, which the grid's row search reads. */
  words:string};
const sides:Readonly<Record<string,string>>={pay_fixed:'Pay fixed',receive_fixed:'Receive fixed',multi_side:'Multi-side'};
/**
 * The position rows: instrument pill, market and delivery, size with lots or the swap's direction, price, mark and value with
 * its measure, or the reason the close cannot value it. A swap's bar side is its backend direction: pay fixed is long.
 */
export function positionRows(holdings:readonly Holding[]):DeskRow[]{
  return holdings.map(h=>{
    const type=instrument(h.instrumentType),value=h[type.measure];
    let side:string,exposure:DeskRow['exposure'];
    if(type===instruments.future){
      if(h.direction!==null)throw Error('The Blotter returned a future with a swap direction.');
      if(typeof h.lots!=='string'||!/^-?\d+$/.test(h.lots))throw Error('The Blotter returned a future without its lots.');
      const lots=h.lots.replace(/^-/,''),unit=lots==='1'?'lot':'lots';
      side=zero(h.lots)?`0 ${unit}`:h.lots.startsWith('-')?`−${lots} ${unit}`:`+${lots} ${unit}`;
      exposure=zero(h.quantity)?'flat':h.quantity.startsWith('-')?'short':'long';
    }else{
      if(typeof h.direction!=='string'||!Object.hasOwn(sides,h.direction))throw Error('The Blotter returned a swap without its direction.');
      side=sides[h.direction];exposure=h.direction==='pay_fixed'?'long':h.direction==='receive_fixed'?'short':null;
    }
    if(h.unavailableReason!==null&&(value!==null||h.mark!==null))throw Error('The Blotter returned a value for a position it cannot value.');
    const price=h[type.price],delivery=deliveryLabel(h.deliveryFrom,h.deliveryTo),unavailable=h.unavailableReason===null?null:valuationReason(h.unavailableReason,h.instrumentType);
    return {tradeId:h.tradeId,revision:h.revision,instrumentType:h.instrumentType,pill:type.pill,reference:h.tradeReference,
      market:curveName(h.curve),commodity:h.commodity.code,commodityName:h.commodity.name,curve:h.curve,delivery,deliveryFrom:h.deliveryFrom,deliveryTo:h.deliveryTo,
      quantity:h.quantity,unit:h.unit,quantityKey:referenceKey(h.quantitySpecification),side,exposure,price,priceLabel:type.priceLabel,mark:h.mark,value,measure:type.label,currency:h.currency,unavailable,
      words:[type.pill,delivery,h.unit,side,price===null?'No fixed price':type.priceLabel,unavailable===null?`${type.label} · ${h.currency}`:`Not valued ${unavailable}`].join(' ')};
  });
}

export type CurveShape={curve:string;unit:string|null;points:{contractId:string;deliveryFrom:string;deliveryTo:string;price:string|null;unavailableReason:string|null}[]|null;unavailableReason:string|null};
export type Sparkline={state:'drawn';points:ShapePoint[];marks:number[];title:string}|{state:'unavailable';reason:string};
const shapeReasons:Readonly<Record<string,string>>={missing_curve:"this market's curve is not in the definitions of this close",missing_curve_view:'no curve view at this close'};
/**
 * A position's sparkline from its market's curve shape: the curve's contracts in the backend's order, a contract the close
 * cannot price kept as a gap, and the contracts of the position's delivery period marked. A curve the close cannot shape says why.
 */
export function curvePoints(shape:CurveShape,row:Pick<DeskRow,'market'|'deliveryFrom'|'deliveryTo'>):Sparkline{
  const unavailable=(why?:string):Sparkline=>({state:'unavailable',reason:why?`Curve shape unavailable · ${why}`:'Curve shape unavailable'});
  if(shape.points===null||typeof shape.unit!=='string')return unavailable(shape.points===null&&typeof shape.unavailableReason==='string'&&Object.hasOwn(shapeReasons,shape.unavailableReason)?shapeReasons[shape.unavailableReason]:undefined);
  if(!shape.points.length)return unavailable('no contracts at this close');
  if(shape.points.every(p=>p.price===null))return unavailable('no prices at this close');
  const points=shape.points.map((p,x)=>({x,y:p.price,label:`${deliveryLabel(p.deliveryFrom,p.deliveryTo)} · ${p.price===null?'no price at this close':`${p.price} ${shape.unit}`}`}));
  const marks=shape.points.flatMap((p,x)=>p.deliveryFrom>=row.deliveryFrom&&p.deliveryTo<=row.deliveryTo?[x]:[]);
  const range=`${deliveryLabel(shape.points[0].deliveryFrom,shape.points[0].deliveryTo)} to ${deliveryLabel(shape.points.at(-1)!.deliveryFrom,shape.points.at(-1)!.deliveryTo)}`;
  return {state:'drawn',points,marks,title:`${row.market} · forward curve, ${range}\n${marks.length?`This position: ${marks.map(x=>points[x].label).join('; ')}`:"This position's delivery is not on the curve at this close"}`};
}
