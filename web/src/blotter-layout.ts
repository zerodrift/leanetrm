import type {GridLayout} from './data-grid.js';
export const priceMeasureChoices=[['executionPriceMin','Minimum execution price'],['executionPriceMax','Maximum execution price'],['executionPriceMean','Arithmetic average execution price · 18 decimal places, ties to even'],['markMin','Minimum curve price'],['markMax','Maximum curve price'],['markMean','Arithmetic average curve price · 18 decimal places, ties to even'],['fixedPriceMin','Minimum fixed price'],['fixedPriceMax','Maximum fixed price'],['fixedPriceMean','Arithmetic average fixed price · 18 decimal places, ties to even']] as const;
export const isPriceMeasure=(key:string)=>priceMeasureChoices.some(c=>c[0]===key);
export const priceMeanPolicy='arithmetic_price_mean_to_even_18_v1';
export const blotterColumns=[['portfolio','Portfolio'],['book','Book'],['market','Market'],['contract','Contract'],['executed','Executed'],['tradeCount','Trades'],['lots','Lots'],['quantity','Quantity'],['unit','Units'],['priceUnit','Price units'],['executionPrice','Execution price'],['mark','Curve price'],['executionPnl','Execution P&L'],['currency','Currency'],['settlement','Settlement'],['tradeReference','Trade ID'],['instrumentReference','Instrument ID'],['revision','Revision'],['instrumentType','Instrument type'],['fixedPrice','Fixed price'],['presentValue','PV'],['traderCode','Trader ID'],['trader','Trader'],['memo','Memo'],['tradeDate','Trade date'],['legalEntity','Legal entity'],['counterparty','Counterparty'],...priceMeasureChoices] as const;
export const groupChoices=[['instrumentType','Instrument type'],['portfolio','Portfolio'],['book','Book'],['market','Market'],['contract','Dated contract']] as const;
export const measureChoices=[['presentValue','PV'],['tradeCount','Trades'],['lots','Lots · group by dated contract'],['quantity','Quantity'],['executionPnl','Execution P&L'],...priceMeasureChoices] as const;
export type BlotterView={version:3;name:string;mode:'trades'|'pivot';columns:string[];bookIds:string[];portfolioIds:string[];groupBy:string[];measures:string[];query:string;columnFilters:Record<string,string>;splitBy:string[];grid:GridLayout;chart:{visible:boolean;measure:string;kind:'bar'|'line'|'heatmap'}};
export const newBlotterView=():BlotterView=>({version:3,name:'Trade rows',mode:'trades',columns:['instrumentType','tradeReference','instrumentReference','revision','book','market','contract','lots','quantity','unit','priceUnit','executionPrice','mark','fixedPrice','presentValue','executionPnl','currency','settlement'],bookIds:[],portfolioIds:[],groupBy:[],measures:[],query:'',columnFilters:{},splitBy:[],grid:{order:['tradeReference','instrumentType'],widths:{},sort:[]},chart:{visible:false,measure:'quantity',kind:'bar'}});
/** The layout without columns that are shown for one read only and never saved in a view. */
export function withoutColumns(layout:GridLayout,keys:ReadonlySet<string>):GridLayout{
 return {order:layout.order.filter(k=>!keys.has(k)),widths:Object.fromEntries(Object.entries(layout.widths).filter(([k])=>!keys.has(k))),sort:layout.sort.filter(s=>!keys.has(s.key))};
}
export function validLayoutField(key:string):boolean{
 if(typeof key!=='string')return false;
 if(blotterColumns.some(c=>c[0]===key))return true;
 if(!key.startsWith('pivot:')||key.length>512)return false;
 try{const [split,measure,...extra]=JSON.parse(key.slice(6));const ids=JSON.parse(split);return !extra.length&&Array.isArray(ids)&&ids.length>0&&ids.length<=3&&ids.every(id=>typeof id==='string'&&/^[a-z0-9_-]{1,80}$/i.test(id))&&measureChoices.some(c=>c[0]===measure);}catch{return false;}
}
export function parseBlotterViews(raw:string):BlotterView[]{
 const rows=JSON.parse(raw);if(!Array.isArray(rows)||rows.length>20)throw Error('Saved blotter views could not be read.');
 const list=(value:unknown,allowed:readonly string[],max=allowed.length):string[]=>{if(!Array.isArray(value)||value.length>max||value.some(x=>typeof x!=='string'||!allowed.includes(x))||new Set(value).size!==value.length)throw Error('Invalid saved view choices.');return [...value];};
 const ids=(v:unknown):string[]=>{if(!Array.isArray(v)||v.length>256||v.some(x=>typeof x!=='string'||!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(x))||new Set(v).size!==v.length)throw Error('Invalid saved book selection.');return [...v];};
 const result=rows.map(v=>{
  if(![1,2,3].includes(v?.version)||typeof v.name!=='string'||!v.name.trim()||v.name.length>60||!['trades','pivot'].includes(v.mode)||typeof v.query!=='string'||v.query.length>200)throw Error('Invalid saved blotter view.');
  const splitBy=v.version===1?[]:list(v.splitBy,groupChoices.map(c=>c[0]));
  const columns=list(v.columns,blotterColumns.map(c=>c[0])),groupBy=list(v.groupBy,groupChoices.map(c=>c[0])),measures=list(v.measures,measureChoices.map(c=>c[0]));
  if(splitBy.some(d=>!groupBy.includes(d))||splitBy.length&&(!groupBy.some(d=>!splitBy.includes(d))||v.mode!=='pivot'))throw Error('Choose separate pivot rows and columns.');
  if(v.mode==='trades'&&columns.some(c=>c==='tradeCount'||isPriceMeasure(c))||v.mode==='pivot'&&(columns.some(c=>![...groupBy,...measures,'instrumentType','unit','currency','priceUnit'].includes(c))||!columns.includes('unit')||!columns.includes('currency')))throw Error('Saved columns must belong to the selected display mode.');
  if(v.mode==='pivot'&&measures.some(isPriceMeasure)&&!columns.includes('priceUnit'))throw Error('Price pivots require their quote units.');
  if(!columns.length||v.mode==='pivot'&&(!groupBy.length||!measures.length||measures.includes('lots')&&!groupBy.includes('contract')))throw Error('Choose pivot dimensions and measures; lots require dated-contract grouping.');
  if(!v.columnFilters||typeof v.columnFilters!=='object'||Array.isArray(v.columnFilters)||Object.entries(v.columnFilters).some(([key,value])=>!validLayoutField(key)||typeof value!=='string'||value.length>200))throw Error('Invalid saved filters.');
  const grid=v.version===1?{order:[],widths:{},sort:[]}:v.grid,chart=v.version===1?{visible:false,measure:'quantity',kind:'bar'}:v.version===2?{...v.chart,kind:'bar'}:v.chart;
  if(!grid||!Array.isArray(grid.order)||grid.order.length>128||grid.order.some((x:unknown)=>typeof x!=='string'||!validLayoutField(x))||new Set(grid.order).size!==grid.order.length||!grid.widths||typeof grid.widths!=='object'||Array.isArray(grid.widths)||Object.entries(grid.widths).some(([key,width])=>!validLayoutField(key)||!Number.isInteger(width)||Number(width)<40||Number(width)>3000)||Object.keys(grid.widths).length>128||!Array.isArray(grid.sort)||grid.sort.length>16||grid.sort.some((x:{key:string;direction:string})=>!x||!validLayoutField(x.key)||!['asc','desc'].includes(x.direction))||new Set(grid.sort.map((x:{key:string})=>x.key)).size!==grid.sort.length)throw Error('Invalid saved grid layout.');
  if(!chart||typeof chart.visible!=='boolean'||!measureChoices.some(c=>c[0]===chart.measure)||!['bar','line','heatmap'].includes(chart.kind))throw Error('Invalid saved chart choices.');
  if(chart.visible&&chart.measure==='lots'&&v.mode==='pivot'&&!groupBy.includes('contract'))throw Error('Lot charts require dated-contract grouping.');
  return {version:3 as const,name:v.name.trim(),mode:v.mode,columns,bookIds:ids(v.bookIds),portfolioIds:ids(v.portfolioIds),groupBy,measures,query:v.query,columnFilters:{...v.columnFilters},splitBy,grid:structuredClone(grid),chart:{visible:chart.visible,measure:chart.measure,kind:chart.kind}};
 });
 if(new Set(result.map(v=>v.name)).size!==result.length)throw Error('Saved view names must be unique.');return result;
}
