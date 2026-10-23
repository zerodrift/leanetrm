import {isPriceMeasure,priceMeasureChoices} from './blotter-layout.js';
import {deliveryLabel,curveName,friendly} from './market-view.js';
import type {ChartSpec} from './chart-view.js';
/** Ordering and reshaping displayed values only; no pricing or aggregation. */
export function compareDecimalStrings(a:string,b:string):number{
  const parts=(value:string):[bigint,bigint]=>{
    if(!/^-?\d+(?:\.\d+|\/[1-9]\d*)?$/.test(value))throw Error('A numeric column requires exact decimal or rational strings.');
    if(value.includes('/')){const [n,d]=value.split('/');return [BigInt(n),BigInt(d)];}
    const [integer,fraction='']=value.split('.');return [BigInt(integer+fraction),10n**BigInt(fraction.length)];
  };
  const [an,ad]=parts(a),[bn,bd]=parts(b),difference=an*bd-bn*ad;return difference===0n?0:difference<0n?-1:1;
}
export type PivotGroup=Record<string,unknown>;
export type MatrixColumn={key:string;title:string;numeric:boolean;measure?:string;groupKey?:string;groupTitle?:string;measureTitle?:string};
const labels:Record<string,string>={instrumentType:'Instrument type',commodity:'Commodity',presentValue:'PV',portfolio:'Portfolio',book:'Book',market:'Market',contract:'Contract',unit:'Units',currency:'Currency',tradeCount:'Trades',lots:'Lots',quantity:'Quantity',executionPnl:'Execution P&L',unvaluedPositions:'Not valued',priceUnit:'Price units',...Object.fromEntries(priceMeasureChoices)};
const canonical=(v:unknown):string=>JSON.stringify(v,(_key,value)=>value&&typeof value==='object'&&!Array.isArray(value)?Object.fromEntries(Object.entries(value).sort(([a],[b])=>a.localeCompare(b))):value);
/** The commodity a group names, as the backend's reference defines it; a group that names none is an error, not a default. */
function commodity(group:PivotGroup){const named=group.commodity as {code?:unknown;name?:unknown}|undefined;if(typeof named?.code!=='string'||typeof named.name!=='string')throw Error('A pivot group did not name its commodity.');return named as {code:string;name:string};}
function identity(group:PivotGroup,dimension:string){return dimension==='instrumentType'?group.instrumentType:dimension==='market'?group.market:dimension==='commodity'?commodity(group).code:group[dimension+'Id'];}
/** How a pivot group names its place on a dimension. */
export function groupLabel(group:PivotGroup,dimension:string){return dimension==='contract'?deliveryLabel(String(group.deliveryFrom),String(group.deliveryTo)):dimension==='market'?curveName(group.market):dimension==='instrumentType'?friendly(String(group.instrumentType)):dimension==='commodity'?commodity(group).name:String(group[dimension]??'');}
export function pivotMatrix(groups:PivotGroup[],dimensions:string[],splitBy:string[],measures:string[]){
  if(!splitBy.length||!dimensions.some(d=>!splitBy.includes(d))||splitBy.some(d=>!dimensions.includes(d))||new Set(dimensions).size!==dimensions.length)throw Error('Choose separate row and column dimensions.');
  const rowsBy=dimensions.filter(d=>!splitBy.includes(d));
  const splits=new Map<string,{title:string;ids:unknown[];sortKey:string}>();
  for(const group of groups){const ids=splitBy.map(d=>identity(group,d));if(ids.some(v=>v===null||v===undefined))throw Error('A pivot dimension identity is missing.');splits.set(canonical(ids),{title:splitBy.map(d=>groupLabel(group,d)).join(' / '),ids,sortKey:canonical(splitBy.map(d=>d==='contract'?[group.deliveryFrom,group.deliveryTo]:groupLabel(group,d)))});}
  const ordered=[...splits.entries()].sort(([a,x],[b,y])=>x.sortKey.localeCompare(y.sortKey)||a.localeCompare(b));
  const columns:MatrixColumn[]=[...new Set([...rowsBy,...(groups.some(g=>g.instrumentType!==undefined)?['instrumentType']:[]),'unit','currency',...(groups.some(g=>g.priceUnit!==undefined)?['priceUnit']:[])])].map(key=>({key,title:labels[key],numeric:false}));
  for(const [key,split] of ordered)for(const measure of measures)columns.push({key:'pivot:'+canonical([key,measure]),title:split.title+' · '+labels[measure],numeric:true,measure,groupKey:key,groupTitle:split.title,measureTitle:labels[measure]});
  if(columns.length>128)throw Error('Choose fewer pivot columns; this view supports up to 128 columns.');
  const rows=new Map<string,Record<string,string|null>>(),filled=new Set<string>();
  for(const group of groups){
    if(!group.quantitySpecification||!group.currencyReference)throw Error('A pivot needs exact quantity and currency partitions.');
    const key=canonical([rowsBy.map(d=>identity(group,d)),group.instrumentType,group.quantitySpecification,group.currencyReference,group.quoteMultiplier??null]);
    const split=canonical(splitBy.map(d=>identity(group,d)));const cell=canonical([key,split]);
    if(filled.has(cell))throw Error('Duplicate pivot cells cannot be combined in the browser.');filled.add(cell);
    let row=rows.get(key);if(!row){row=Object.fromEntries(columns.map(c=>[c.key,null]));for(const d of rowsBy)row[d]=groupLabel(group,d);if(group.instrumentType!==undefined)row.instrumentType=friendly(String(group.instrumentType));row.unit=String(group.unit);row.currency=String(group.currency);if(group.priceUnit!==undefined)row.priceUnit=String(group.priceUnit);rows.set(key,row);}
    for(const measure of measures){const value=group[measure];if(value===undefined)throw Error('A selected pivot measure is missing.');row['pivot:'+canonical([split,measure])]=value===null?null:String(value);}
  }
  return {columns,rows:[...rows.values()]};
}

/** Project backend pivot cells into plots. No browser aggregation or conversion. */
export function pivotChartPanels(groups:PivotGroup[],dimensions:string[],splitBy:string[],measure:string):ChartSpec[]{
  const categoriesBy=splitBy.length?splitBy:dimensions,seriesBy=splitBy.length?dimensions.filter(d=>!splitBy.includes(d)):[];
  if(!categoriesBy.length||splitBy.some(d=>!dimensions.includes(d)))throw Error('Choose chart dimensions from the pivot axes.');
  const id=(group:PivotGroup,keys:string[])=>{const values=keys.map(d=>identity(group,d));if(values.some(v=>v===null||v===undefined))throw Error('A chart dimension identity is missing.');return canonical(values);};
  const partitions=new Map<string,PivotGroup[]>();
  for(const group of groups){
    if(!group.quantitySpecification||!group.currencyReference)throw Error('A chart needs exact quantity and currency partitions.');
    const key=canonical([group.instrumentType,group.quantitySpecification,group.currencyReference,isPriceMeasure(measure)?group.quoteMultiplier:null]);
    const rows=partitions.get(key)??[];rows.push(group);partitions.set(key,rows);
  }
  return [...partitions.values()].map(rows=>{
    const categories=[...new Map(rows.map(g=>[id(g,categoriesBy),g])).entries()].sort(([a,x],[b,y])=>{
      const sort=(g:PivotGroup)=>canonical(categoriesBy.map(d=>d==='contract'?[g.deliveryFrom,g.deliveryTo]:groupLabel(g,d)));
      return sort(x).localeCompare(sort(y))||a.localeCompare(b);
    });
    const series=new Map<string,Map<string,PivotGroup>>();
    for(const row of rows){
      const key=id(row,seriesBy),cells=series.get(key)??new Map<string,PivotGroup>(),category=id(row,categoriesBy);
      if(cells.has(category))throw Error('Duplicate chart cells cannot be combined in the browser.');
      if(row[measure]===undefined)throw Error('The selected chart measure is missing.');
      cells.set(category,row);series.set(key,cells);
    }
    const first=rows[0],unit=isPriceMeasure(measure)?String(first.priceUnit):measure==='quantity'?String(first.unit):measure==='tradeCount'?'Trades':measure==='lots'?'Lots':String(first.currency);
    return {title:labels[measure],subtitle:[friendly(String(first.instrumentType??'')),String(first.unit),String(first.currency),isPriceMeasure(measure)?String(first.priceUnit):''].filter(Boolean).join(' · '),
      axis:categoriesBy.length===1&&categoriesBy[0]==='contract'?'delivery':'category',categoryTitle:categoriesBy.map(d=>labels[d]).join(' / '),unit,
      series:[...series.entries()].map(([key,cells])=>{const first=cells.values().next().value!;return {id:key,name:seriesBy.length?seriesBy.map(d=>groupLabel(first,d)).join(' / '):labels[measure],points:categories.map(([x,row])=>({x,label:categoriesBy.map(d=>groupLabel(row,d)).join(' / '),y:cells.has(x)&&cells.get(x)![measure]!==null?String(cells.get(x)![measure]):null}))};})};
  });
}
