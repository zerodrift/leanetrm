// What the Blotter desk shows: which backend groups and rows each tile, chart, group header and position row
// presents, and in which words. It selects, orders and labels exact server values; it never adds, nets, converts
// or infers them (AGENTS.md: TypeScript is presentation only).
import {compareDecimalStrings,type PivotGroup} from './analytics-model.js';
import {amountText,type DisplayPrecision} from './amount-display.js';
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
  lots:string|null;quantity:string|null;unit:string;quantitySpecification:Reference;executedAt:string;executionPrice:string|null;fixedPrice:string|null;mark:string|null;markMinusPrice:string|null;
  executionPnl:string|null;presentValue:string|null;currency:string;currencyReference:Reference;settlement:string;direction:'pay_fixed'|'receive_fixed'|'multi_side'|null;unavailableReason:ValuationReason|null;
  /** A swap's instrument kind: a scheduled one (D214) holds its quantities and prices by period. Futures state none. */
  contractIdentityKind?:'swap_instrument'|'scheduled_swap_instrument';
  /** For a scheduled swap whose periods project from several curves: the markets together, as the backend keys and names them. */
  marketKey?:string|null;marketLabel?:string|null;projectionCurves?:string[]|null};
export type Summary={view:{groupBy:string[];measures:string[];pivotConvention:string|null};groups:PivotGroup[]};

const moneyTotals='trading_blotter_money_totals_v1',quantityTotals='trading_blotter_totals_v1';
const moneyMeasures=['presentValue','executionPnl','tradeCount'];
// The desk's groupings of its one read: tile totals and splits as money per currency (D220), the position mix
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
/** The value cells an instrument type shows: its mark and its own value measure; a position the close cannot value says why in them. */
export const valueFields=(instrumentType:string)=>['mark',instrument(instrumentType).measure];
function instrument(type:unknown){if(typeof type!=='string'||!Object.hasOwn(instruments,type))throw Error('The Blotter returned an unknown instrument type.');return instruments[type];}

/** The start of each id, at least eight characters and never cut at a separator, that no other id of the set starts with. */
function shortIds(ids:Iterable<string>):Map<string,string>{
  const all=[...new Set(ids)];
  return new Map(all.map(id=>{
    let length=Math.min(8,id.length);
    while(length<id.length&&(/[-:]$/.test(id.slice(0,length))||all.some(other=>other!==id&&other.startsWith(id.slice(0,length)))))length++;
    return [id,id.slice(0,length)];
  }));
}
type Identity={label:string;code:string;revision:string;catalog?:string};
/**
 * Distinct labels for distinct keys. Keys whose labels collide are told apart by what identifies them, never by a position
 * among them: their codes where these differ, and for one code in several revisions the start of each revision's id. A revision
 * id is its own: two references that share one are in different catalogs, and are named by the catalog's id and the revision's.
 */
function distinct(entries:ReadonlyMap<string,Identity>,word:string):Map<string,string>{
  const sharing=new Map<string,string[]>();
  for(const [key,{label}] of entries)sharing.set(label,[...(sharing.get(label)??[]),key]);
  return new Map([...entries].map(([key,{label,code,revision}])=>{
    const colliding=sharing.get(label)!.map(k=>entries.get(k)!);if(colliding.length===1)return [key,label];
    const own=new Set(colliding.map(e=>e.revision)).size===colliding.length,identity=(e:Identity)=>own?e.revision:`${e.catalog}:${e.revision}`;
    const short=shortIds(colliding.map(identity));
    return [key,[label,...(new Set(colliding.map(e=>e.code)).size>1?[code]:[]),...(colliding.filter(e=>e.code===code).length>1?[`${word} ${short.get(identity(entries.get(key)!))}`]:[])].join(' · ')];
  }));
}
// A reference is identified by its code in its catalog's revision; a key built from it has been validated by referenceKey.
const referenceIdentity=(label:string,reference:unknown):Identity=>{const {code,revision}=reference as Reference;return {label,code,revision:revision.revisionId,catalog:revision.catalogId};};
const currencyLabels=(groups:readonly PivotGroup[])=>distinct(new Map(groups.map(g=>[referenceKey(g.currencyReference),referenceIdentity(text(g,'currency'),g.currencyReference)])),'revision');
const unitLabels=(groups:readonly PivotGroup[])=>distinct(new Map(groups.map(g=>[referenceKey(g.quantitySpecification),referenceIdentity(text(g,'unit'),g.quantitySpecification)])),'revision');
const marketKey=(market:string,revision:string)=>`${market}|${revision}`;
// The market a position is grouped under: its curve, or the several curves of a scheduled swap together as the backend keys them.
const marketOf=(h:Pick<Holding,'curve'|'marketKey'|'definitionRevisionId'>)=>marketKey(h.marketKey??h.curve,h.definitionRevisionId);
// A market's name: its curve's, or the backend's name of several curves together.
const marketName=(group:PivotGroup)=>typeof group.marketLabel==='string'?group.marketLabel:curveName(text(group,'market'));
// A market is identified by its code and definition revision; only names that two such markets share say which.
const marketLabels=(groups:readonly PivotGroup[])=>distinct(new Map(groups.map(g=>[marketKey(text(g,'market'),text(g,'definitionRevisionId')),{label:marketName(g),code:text(g,'market'),revision:text(g,'definitionRevisionId')}])),'definition');

/** What a total leaves out: nothing, some positions, or every position, when it has no value at all. */
function totalNote(total:string|null,excluded:number):string|null{
  if(total!==null)return valuationNote(excluded);
  if(!excluded)throw Error('The Blotter returned a total without a value or a reason.');
  return `No valued positions · ${excluded} excluded`;
}

export type TileRow={currencyKey:string;currency:string;total:string|null;trades:number;excluded:number;note:string|null;byBook:{bookId:string;book:string;value:string|null;excluded:number}[]};
/** A money tile: one row per exact currency, never added across currencies. `note` is the tile's own when it has one row. */
export type Tile={key:'swapPv'|'futuresPnl';rows:TileRow[];note:string|null};
/** Swap PV and futures execution P&L, each per currency and split by book, from the money totals (D220). */
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
  const groups=groupsOf(summaries,mix),units=unitLabels(groups),currencies=currencyLabels(groups);
  const entries=groups.map(g=>{const commodity=commodityOf(g),type=text(g,'instrumentType');
    return {key:JSON.stringify([commodity.code,type,referenceKey(g.quantitySpecification),referenceKey(g.currencyReference)]),commodity:commodity.code,commodityName:commodity.name,instrumentType:type,
      base:`${commodity.name} · ${instrument(type).plural}`,unit:units.get(referenceKey(g.quantitySpecification))!,currency:currencies.get(referenceKey(g.currencyReference))!,trades:count(g,'tradeCount')};});
  // Segments of one commodity and instrument type say what keeps them apart: their unit, their currency, or both.
  const label=(e:typeof entries[number])=>{const same=entries.filter(o=>o.base===e.base);if(same.length===1)return e.base;
    return [e.base,...(new Set(same.map(o=>o.unit)).size>1?[e.unit]:[]),...(new Set(same.map(o=>o.currency)).size>1?[e.currency]:[])].join(' · ');};
  // Two commodities that share a name are told apart by their codes.
  const labels=distinct(new Map(entries.map(e=>[e.key,{label:label(e),code:e.commodity,revision:e.key}])),'group');
  const segments=entries.map(({key,commodity,commodityName,instrumentType,trades})=>({key,commodity,commodityName,instrumentType,label:labels.get(key)!,trades}))
    .sort((a,b)=>a.label.localeCompare(b.label)||byText(a.key,b.key));
  if(!Number.isInteger(rows)||rows<0)throw Error(inconsistent);
  const excluded=excludedPositions(unvalued);
  return {count:rows,segments,note:excluded?`Includes ${excluded} position${excluded===1?'':'s'} not valued at this close`:null};
}

export type Subtotal={measure:'PV'|'P&L';amount:string|null;currency:string;excluded:number;note:string|null};
export type GroupHeader={key:string;label:string;positions:number;subtotals:Subtotal[]};
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
    const header=headers.get(key)??{key,label:labelOf(group),positions:0,subtotals:[]};
    // The market summary keeps quantity specifications and currencies apart (D98), so a measure of a market that appears twice is
    // one instrument type held in more than one of them, and the header would state that measure twice. That is refused, as the
    // charts refuse a market they would draw as two bars of one name. A market's futures and swaps are two measures and may differ in both.
    if(rowGroup==='market'&&header.subtotals.some(s=>s.measure===label))throw Error(inconsistent);
    header.subtotals.push({measure:label,amount:total,currency:currencies.get(referenceKey(group.currencyReference))!,excluded,note:totalNote(total,excluded)});headers.set(key,header);
  }
  const keys=holdings.map(h=>rowGroup==='book'?h.bookId:rowGroup==='market'?marketOf(h):h.instrumentType);
  if(keys.some(key=>!headers.has(key)))throw Error(missingSummaries);
  const order=['PV','P&L'],sizes=new Map<string,number>();
  for(const key of keys)sizes.set(key,(sizes.get(key)??0)+1);
  return {keys,headers:[...headers.values()].filter(h=>sizes.has(h.key))
    .map(h=>({...h,positions:sizes.get(h.key)!,subtotals:h.subtotals.sort((a,b)=>order.indexOf(a.measure)-order.indexOf(b.measure)||a.currency.localeCompare(b.currency))}))
    .sort((a,b)=>a.label.localeCompare(b.label)||byText(a.key,b.key))};
}

/** The display text of an exact amount, as the backend formats it for the row or the read. */
export type Format=(value:string)=>string;
/** How one position's amounts read: each by the precision and increment of its own deal, as its row carries them. */
export type HoldingFormats={money:Format;quantity:Format;price:Format;mark:Format;markMinusPrice:Format};
export function holdingFormats(h:Holding):HoldingFormats{
  // A swap's price is its fixed price, which has a precision of its own; a mark, and a mark less a price, take the contract's clearing increment.
  const price=h.instrumentType==='financial_swap'?h.fixedAmountFormat:h.amountFormat;
  return {money:value=>amountText(value,'money',undefined,h.amountFormat),quantity:value=>amountText(value,'quantity',undefined,h.amountFormat),
    price:value=>amountText(value,'price',h.priceTick,price),mark:value=>amountText(value,'price',h.markTick,h.amountFormat),markMinusPrice:value=>amountText(value,'price',h.markTick,h.amountFormat)};
}

/** How an exact amount's sign reads, for color and for its text: gain, loss or neither. */
export const signOf=(value:string):'positive'|'negative'|'zero'=>/^-?0+(?:\.0+)?(?:\/\d+)?$/.test(value)?'zero':value.startsWith('-')?'negative':'positive';
/** A display amount with its sign always shown: + for a gain, − for a loss, none for zero. */
export function signedText(value:string,format:Format):string{
  const shown=format(value),sign=signOf(value);
  return sign==='zero'?shown:sign==='negative'?'−'+shown.replace(/^-/,''):'+'+shown;
}
/** A display amount whose own minus sign is the typographic one. */
export const minus=(text:string)=>text.replace(/^-/,'−');

/**
 * Swap PV by market: one horizontal panel per currency, gains right and losses left, largest gain first, each market
 * marked with its commodity's color. The panel is captioned by its currency and says what it leaves out with that currency's
 * own backend count. A market in two units of one currency would be two bars of one name: that is refused.
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
    if(new Set(points.map(p=>p.x)).size!==points.length)throw Error(inconsistent);
    const note=totalNote(amount(total[0],'presentValue'),excludedPositions(total[0].unvaluedPositions)),unit=currencies.get(currency)!;
    points.sort((a,b)=>a.y===null?(b.y===null?a.label!.localeCompare(b.label!):1):b.y===null?-1:compareDecimalStrings(b.y,a.y)||a.label!.localeCompare(b.label!));
    return {unit,spec:{title:`${unit} · bar right = gain`,...(note?{subtitle:note}:{}),axis:'category',categoryTitle:'Market',unit,kind:'bar',orientation:'horizontal',colorBySign:true,
      series:[{name:'Swap PV',points}]} satisfies ChartSpec};
  }).sort((a,b)=>a.unit.localeCompare(b.unit)).map(p=>p.spec);
}

/**
 * Long and short by market: one horizontal panel per quantity unit, each bar in its commodity's color, captioned by its unit.
 * A future's backend quantity is signed. A swap's is its unsigned booked quantity, so its bar takes the side the backend's
 * direction states (pay fixed long, receive fixed short) where every swap of that market and unit shares one, and where the
 * market has one quantity, which it has not where a swap of it is scheduled by period; otherwise it is not drawn, and its panel
 * says why. A market held in two currencies of one unit would be two bars of one name: that is refused.
 */
export function positionChartPanels(summaries:readonly Summary[],holdings:readonly Holding[],colors:ReadonlyMap<string,string>,format:(value:string)=>string):ChartSpec[]{
  const markets=groupsOf(summaries,byMarket),labels=marketLabels(markets),units=unitLabels(markets);
  // The directions the swaps of each market, unit and currency state, and which of them hold a swap without a single quantity, from one
  // pass over the positions.
  const directions=new Map<string,Set<Holding['direction']>>(),unquantified=new Set<string>();
  for(const h of holdings){
    if(instrument(h.instrumentType)!==instruments.financial_swap)continue;
    const key=JSON.stringify([marketOf(h),referenceKey(h.quantitySpecification),referenceKey(h.currencyReference)]);
    directions.set(key,(directions.get(key)??new Set()).add(h.direction));
    if(h.quantity===null)unquantified.add(key);
  }
  const panels=new Map<string,ChartPoint[]>();
  for(const group of markets){
    const market=marketKey(text(group,'market'),text(group,'definitionRevisionId')),type=instrument(group.instrumentType),unit=referenceKey(group.quantitySpecification),currency=referenceKey(group.currencyReference);
    const quantity=amount(group,'quantity');
    const point:ChartPoint={x:`${market}|${type.pill}`,label:`${labels.get(market)} · ${type.plural}`,y:quantity,color:colors.get(commodityOf(group).code)};
    // A market's swaps have no single quantity where one of them is scheduled by period, which a held swap without one shows; a future
    // always has one.
    if(quantity===null){if(type===instruments.future||!unquantified.has(JSON.stringify([market,unit,currency])))throw Error(inconsistent);point.label+=' · no single quantity';}
    else if(type===instruments.future)point.text=signedText(quantity,format);
    else{
      const stated=directions.get(JSON.stringify([market,unit,currency]));
      if(!stated)throw Error(missingSummaries);
      const [only]=stated;
      if(stated.size===1&&(only==='pay_fixed'||only==='receive_fixed')){point.direction=only==='pay_fixed'?'positive':'negative';point.text=`${format(quantity)} ${only==='pay_fixed'?'pay':'receive'} fixed`;}
      else{point.y=null;point.label+=stated.size===1&&only==='multi_side'?' · multi-side':' · mixed directions';}
    }
    panels.set(unit,[...(panels.get(unit)??[]),point]);
  }
  return [...panels].map(([unit,points])=>({unit:units.get(unit)!,points:points.sort((a,b)=>a.label!.localeCompare(b.label!))}))
    .sort((a,b)=>a.unit.localeCompare(b.unit))
    .map(({unit,points})=>{
      if(new Set(points.map(p=>p.x)).size!==points.length)throw Error(inconsistent);
      // The markets a panel draws no bar for, with why, which a panel with no bar left would not otherwise show.
      const undrawn=points.filter(p=>p.y===null).map(p=>p.label);
      return {title:`${unit} · bar right = long`,...(undrawn.length?{subtitle:`Not drawn: ${undrawn.join('; ')}`}:{}),axis:'category',categoryTitle:'Market',unit,kind:'bar',orientation:'horizontal',
        series:[{name:'Quantity',points}]} satisfies ChartSpec;
    });
}

// The design's commodity identity colors (blotter-desk.md, Visual system): gas and power, under the explicit energy codes of
// the imported reference catalog and under those of the synthetic one.
const identity:Readonly<Record<string,string>>={gas:palette[2],electricity:palette[1],test_gas:palette[2],test_power:palette[1]};
/** A color for each commodity: its identity color, or the next unused categorical color in code order. Marks always name it too. */
export function commodityColors(codes:Iterable<string>):Map<string,string>{
  const free=palette.filter(color=>!Object.values(identity).includes(color));let next=0;
  return new Map([...new Set(codes)].sort(byText).map(code=>[code,Object.hasOwn(identity,code)?identity[code]:free[next++%free.length]]));
}

/** A position the close values states its value; one it cannot value states why, and has no value. */
type Valuation={value:string;unavailable:null}|{value:null;unavailable:string};
export type DeskRow={tradeId:string;revision:string;instrumentType:string;pill:'FUT'|'SWAP';reference:string;
  market:string;commodity:string;commodityName:string;curve:string;delivery:string;deliveryFrom:string;deliveryTo:string;
  quantity:string|null;unit:string;quantityKey:string;side:string;exposure:'long'|'short'|'flat'|null;
  price:string|null;priceLabel:'Executed'|'Fixed';priceUnit:string;mark:string|null;measure:'P&L'|'PV';currency:string;
  /**
   * Why the position has no single quantity, fixed price or mark the backend states, where it has none for a reason of its own: a
   * scheduled swap holds them by period, and a swap valued by its settlement lifecycle states no curve mark.
   */
  quantityNote:string|null;priceNote:string|null;markNote:string|null;
  /** Whether the position projects from several curves together, as a scheduled swap can: it then has no one curve of its own. */
  severalCurves:boolean;
  /** The words its cells draw beside their exact amounts, which the grid's row search reads. */
  words:string}&Valuation;
/** The unit of a position's price and mark: its currency per quantity unit, with the quote multiplier when it is not one. */
export const priceUnit=(h:{currency:string;unit:string;quoteMultiplier?:string})=>`${h.currency}/${h.unit}${h.quoteMultiplier&&h.quoteMultiplier!=='1'?' × '+h.quoteMultiplier:''}`;
const sides:Readonly<Record<string,string>>={pay_fixed:'Pay fixed',receive_fixed:'Receive fixed',multi_side:'Multi-side'};
// The kinds of swap instrument and the valuation states of a swap the Blotter states; any other is refused, never shown as one of them.
const swapKinds:readonly unknown[]=['swap_instrument','scheduled_swap_instrument'],swapValuations:readonly unknown[]=['Projection','Active'];
const byPeriod='By period';
// What each note in place of a figure means, in full: the words a cell's tooltip and the position panel give it.
const notes:Readonly<Record<string,string>>={
  [byPeriod]:'This scheduled swap holds it by period: the panel\'s Terms list each period.',
  'Several marks':'No single curve price for this position at this close.',
  'Settlement lifecycle':'Valued by its settlement lifecycle, which states no single curve price.',
};
/** Whether a position projects from several curves together, as a scheduled swap can (D214): it then has no one curve of its own. */
export const projectsFromSeveralCurves=(h:Pick<Holding,'projectionCurves'>)=>(h.projectionCurves?.length??0)>1;
/** Why such a position has no curve shape to draw: no one curve is its own. */
export const severalCurvesShape='Curve shape unavailable · this position projects from several curves';
/** What a row's note in place of a figure means. */
export function noteMeaning(note:string){if(!Object.hasOwn(notes,note))throw Error('The desk has no meaning for this note.');return notes[note];}
/**
 * A position's size as the desk shows it: a future's quantity carries its sign; a swap's booked quantity has none, and its side label
 * states its direction. A scheduled swap has no single quantity, and says why.
 */
export function quantityText(row:Pick<DeskRow,'instrumentType'|'quantity'|'quantityNote'>,format:Format){
  if(row.quantity===null){if(row.quantityNote===null)throw Error('A position without a quantity must say why.');return row.quantityNote;}
  return row.instrumentType==='future'?signedText(row.quantity,format):minus(format(row.quantity));
}
/**
 * The position rows: instrument pill, market and delivery, size with lots or the swap's direction, price and mark in their unit,
 * and value with its measure, or the reason the close cannot value it. A valued position states its value, and a valued
 * future its mark; a swap without one mark says why. A swap's bar side is its backend direction: pay fixed is long. A scheduled
 * swap (D214) has its quantities, prices and marks by period, and the market of the curves its periods project from together.
 */
export function positionRows(holdings:readonly Holding[]):DeskRow[]{
  return holdings.map(h=>{
    const type=instrument(h.instrumentType),value=h[type.measure];
    let side:string,exposure:DeskRow['exposure'],scheduledSwap=false;
    if(type===instruments.future){
      if(h.direction!==null)throw Error('The Blotter returned a future with a swap direction.');
      if(typeof h.lots!=='string'||!/^-?\d+$/.test(h.lots))throw Error('The Blotter returned a future without its lots.');
      if(h.quantity===null)throw Error('The Blotter returned a future without its quantity.');
      const lots=h.lots.replace(/^-/,''),unit=lots==='1'?'lot':'lots';
      side=signOf(h.lots)==='zero'?`0 ${unit}`:h.lots.startsWith('-')?`−${lots} ${unit}`:`+${lots} ${unit}`;
      exposure=signOf(h.quantity)==='zero'?'flat':h.quantity.startsWith('-')?'short':'long';
    }else{
      if(typeof h.direction!=='string'||!Object.hasOwn(sides,h.direction))throw Error('The Blotter returned a swap without its direction.');
      if(!swapKinds.includes(h.contractIdentityKind))throw Error('The Blotter returned a swap without its instrument kind.');
      if(!swapValuations.includes(h.settlement))throw Error('The Blotter returned a swap in an unknown valuation state.');
      scheduledSwap=h.contractIdentityKind==='scheduled_swap_instrument';
      if(scheduledSwap!==(h.quantity===null))throw Error(scheduledSwap?'The Blotter returned a single quantity for a scheduled swap.':'The Blotter returned a swap without its quantity.');
      side=sides[h.direction];exposure=h.quantity===null?null:h.direction==='pay_fixed'?'long':h.direction==='receive_fixed'?'short':null;
    }
    let valuation:Valuation;
    if(h.unavailableReason!==null){
      if(value!==null||h.mark!==null)throw Error('The Blotter returned a value for a position it cannot value.');
      valuation={value:null,unavailable:valuationReason(h.unavailableReason,h.instrumentType)};
    }else{
      if(value===null)throw Error('The Blotter returned a position without a value or a reason.');
      if(h.mark===null&&type===instruments.future)throw Error('The Blotter returned a valued future without its mark.');
      valuation={value,unavailable:null};
    }
    const price=h[type.price],unitOfPrice=priceUnit(h),delivery=deliveryLabel(h.deliveryFrom,h.deliveryTo);
    // A scheduled swap states a fixed price only where every fixed period shares it; two floating sides have none at all.
    const quantityNote=h.quantity===null?byPeriod:null,priceNote=scheduledSwap&&price===null&&h.direction!=='multi_side'?byPeriod:null;
    const markNote=valuation.unavailable!==null||h.mark!==null?null:h.settlement==='Active'?'Settlement lifecycle':scheduledSwap?byPeriod:'Several marks';
    return {tradeId:h.tradeId,revision:h.revision,instrumentType:h.instrumentType,pill:type.pill,reference:h.tradeReference,
      market:h.marketLabel??curveName(h.curve),commodity:h.commodity.code,commodityName:h.commodity.name,curve:h.curve,delivery,deliveryFrom:h.deliveryFrom,deliveryTo:h.deliveryTo,
      quantity:h.quantity,unit:h.unit,quantityKey:referenceKey(h.quantitySpecification),side,exposure,price,priceLabel:type.priceLabel,priceUnit:unitOfPrice,mark:h.mark,measure:type.label,currency:h.currency,...valuation,
      quantityNote,priceNote,markNote,severalCurves:projectsFromSeveralCurves(h),
      words:[type.pill,delivery,...(quantityNote?[quantityNote]:[]),h.unit,side,...(priceNote?[priceNote]:[]),price===null&&priceNote===null?'No fixed price':`${type.priceLabel} · ${unitOfPrice}`,...(markNote?[markNote]:[]),
        valuation.unavailable===null?`${type.label} · ${h.currency}`:`Not valued ${valuation.unavailable}`].join(' ')};
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

/** What a curve's chart is for: the market, and the delivery period whose contracts it marks, as a row or a draft states them. */
export type CurveTarget=Pick<DeskRow,'market'|'delivery'|'deliveryFrom'|'deliveryTo'>;
/** A market's forward curve drawn at the close, with the contracts of a delivery period marked; or why it cannot be drawn. */
export type CurveChart={state:'chart';spec:ChartSpec;marked:boolean}|{state:'unavailable';reason:string};
/**
 * The chart of a market's curve shape, in the contracts' own order, a contract the close cannot price kept as a gap, and the
 * contracts of the target's delivery period marked. A curve the close cannot shape says why, as a row's sparkline does.
 */
export function curveChart(shape:CurveShape,target:CurveTarget):CurveChart{
  const line=curvePoints(shape,target);
  if(line.state==='unavailable')return line;
  const points:ChartPoint[]=shape.points!.map(p=>({x:p.contractId,label:deliveryLabel(p.deliveryFrom,p.deliveryTo),y:p.price}));
  const spec:ChartSpec={title:`${target.market} · forward curve`,axis:'delivery',unit:shape.unit!,kind:'line',series:[{name:'Curve at this close',points}],marked:line.marks.map(i=>points[i].x)};
  if(spec.marked!.length)spec.subtitle=`Marked: delivery ${target.delivery}`;
  return {state:'chart',spec,marked:spec.marked!.length>0};
}
