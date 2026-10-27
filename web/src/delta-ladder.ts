// The desk's delta ladder (delta-ladder.md, D230): a risk run's per-factor totals exactly as the server returns them, one row for each
// shocked factor and one column for each book, measure and currency. Nothing here adds, converts or infers a value: a row is its own
// shock and rows overlap (a computed point repeats the exposure of the quotes it is built from), so the ladder has no totals.
import {compareDecimalStrings} from './analytics-model.js';
import {deliveryLabel} from './market-view.js';
import {signOf} from './desk-model.js';
import {positionKey} from './risk-lens.js';
import type {RiskReport,RiskTotal} from './risk-report.js';

/** Which of a total's values the ladder shows: its delta per one unit of the factor's price, or its P&L if the factor moves up or down. */
export type LadderValue='delta'|'upPnl'|'downPnl';
export type LadderColumn={key:string;title:string;bookId:string;book:string;measure:string;currency:string};
export type LadderCell={missing:true}|{missing?:false;text:string;exact:string;unit:string;zero:boolean;nonlinear:boolean;up:string;down:string;upExact:string;downExact:string};
export type LadderRow={factorId:string;period:string;coordinate:string;shock:string;priceUnit:string;cells:LadderCell[]};
export type LadderGroup={key:string;title:string;rows:LadderRow[]};
export type Ladder={columns:LadderColumn[];groups:LadderGroup[]};

const coordinates:Readonly<Record<string,string>>={market_quote:'Quote',computed_output:'Computed point'};
const columnKey=(t:RiskTotal)=>JSON.stringify([t.bookId,t.measure,t.values.currency]);

/**
 * The ladder of a run's reports, its parts in the order they ran. Columns follow the order the server first returns each book, measure
 * and currency in; groups follow the order of their curves' first factors. A factor returned twice, or two totals for one column of a
 * factor, is refused rather than merged; a curve `curveName` cannot name refuses the ladder rather than showing a code in its place.
 */
export function deltaLadder(reports:readonly Pick<RiskReport,'factors'>[],value:LadderValue,curveName:(code:string)=>string):Ladder{
  const factors=reports.flatMap(r=>r.factors);
  if(new Set(factors.map(f=>f.factor.id)).size!==factors.length)throw Error('This run returns a shocked factor more than once, so its ladder is not shown. Run it again.');
  const columns=new Map<string,LadderColumn>();
  for(const t of factors.flatMap(f=>f.totals)){
    const key=columnKey(t);
    if(!columns.has(key))columns.set(key,{key,title:`${t.book} · ${t.measureLabel} · ${t.values.currency}`,bookId:t.bookId,book:t.book,measure:t.measure,currency:t.values.currency});
  }
  const groups=new Map<string,LadderGroup>();
  for(const {factor,bump,totals} of factors){
    const byColumn=new Map(totals.map(t=>[columnKey(t),t]));
    if(byColumn.size!==totals.length)throw Error('This run returns two totals for one book, measure and currency of a factor, so its ladder is not shown. Run it again.');
    const key=factor.curves.join('+');
    const group=groups.get(key)??{key,title:factor.curves.map(curveName).join(' + '),rows:[]};groups.set(key,group);
    group.rows.push({factorId:factor.id,period:factor.deliveryFrom&&factor.deliveryTo?deliveryLabel(factor.deliveryFrom,factor.deliveryTo):factor.label,
      coordinate:coordinates[factor.coordinate]??factor.coordinate,shock:`±${bump} ${factor.unit}`,priceUnit:factor.unit,
      cells:[...columns.keys()].map(column=>{
        const total=byColumn.get(column);
        if(!total)return {missing:true};
        const {values}=total;
        return {text:values.display[value],exact:values[value],unit:value==='delta'?values.deltaUnit:values.currency,zero:signOf(values[value])==='zero',
          nonlinear:signOf(values.gamma)!=='zero',up:values.display.upPnl,down:values.display.downPnl,upExact:values.upPnl,downExact:values.downPnl};
      })});
  }
  return {columns:[...columns.values()],groups:[...groups.values()]};
}

const valueWords:Readonly<Record<LadderValue,string>>={delta:'Delta per 1 price unit',upPnl:'Up P&L',downPnl:'Down P&L'};
/** The ladder as a spreadsheet states it: a row for each factor, its exact amounts, and the unit of every value in its headers and rows. */
export function ladderExport(ladder:Ladder,value:LadderValue):{headers:string[];rows:string[][];numeric:number[]}{
  const head=['Curve','Contract','Coordinate','Shock','Price unit','Value'];
  return {headers:[...head,...ladder.columns.map(c=>c.title)],numeric:ladder.columns.map((_,i)=>head.length+i),
    rows:ladder.groups.flatMap(g=>g.rows.map(r=>[g.title,r.period,r.coordinate,r.shock,r.priceUnit,valueWords[value],...r.cells.map(c=>c.missing?'Not reported':c.exact)]))};
}

export type ContractPosition={key:string;tradeReference:string;label:string;text:string;exact:string;up:string;down:string;unit:string};
/**
 * What a ladder row's shock moves: each position whose chosen value under it is not zero, as the server values it, by its book and
 * measure, largest magnitude first (compared exactly, never converted), and how many it does not move.
 */
export function contractPositions(factor:RiskReport['factors'][number],value:LadderValue):{positions:ContractPosition[];unmoved:number}{
  const moved=factor.positions.filter(p=>signOf(p.values[value])!=='zero');
  const magnitude=(v:string)=>v.replace(/^-/,'');
  return {unmoved:factor.positions.length-moved.length,
    positions:moved.sort((a,b)=>compareDecimalStrings(magnitude(b.values[value]),magnitude(a.values[value]))||a.tradeReference.localeCompare(b.tradeReference))
      .map(p=>({key:positionKey(p),tradeReference:p.tradeReference,label:`${p.book} · ${p.measureLabel}`,text:p.values.display[value],exact:p.values[value],up:p.values.display.upPnl,down:p.values.display.downPnl,
        unit:value==='delta'?p.values.deltaUnit:p.values.currency}))};
}
