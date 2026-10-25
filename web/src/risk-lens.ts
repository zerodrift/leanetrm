// The risk lens of the Blotter desk (blotter-desk.md, Risk report inline; D220): what a run's results say of each position, of the run
// as a whole and of its change chart, as the desk shows them beside the positions. Each factor's results are its own, so this picks and
// labels the backend's exact strings and never adds, nets or scales them across factors, parts or positions.
import {compareDecimalStrings} from './analytics-model.js';
import type {ChartPoint,ChartSpec} from './chart-view.js';
import {signOf,signedText} from './desk-model.js';
import {displayInstant} from './market-view.js';
import type {ExcludedPosition,RiskReport} from './risk-report.js';
import type {RunView} from './risk-view.js';
import {valuationReason} from './valuation-status.js';

/** A position as the lens names it: by its trade and its binding, as the side panel does. */
export const positionKey=(position:{tradeId:string;bindingId:string})=>JSON.stringify([position.tradeId,position.bindingId]);

/** One factor's results for a position it moves: the shock and its unit, the changes, delta and gamma, and the base, up and down values. */
export type LensShock={factor:string;label:string;bump:string;unit:string;up:string;down:string;delta:string;deltaUnit:string;gamma:string;gammaUnit:string;
  base:string;upValue:string;downValue:string;measureLabel:string;currency:string;currencyKey:string};
/** What a run says of one position: each shock that moves it, in the order the run made them, and nothing combined from them. */
export type LensCell={tradeReference:string;shocks:LensShock[]};

const byText=(a:string,b:string)=>a<b?-1:a>b?1:0;
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>byText(a,b))):v);
const plural=(count:number,word:string)=>`${count} ${word}${count===1?'':'s'}`;

/**
 * Each position a run values, by its key, with the shocks that move it: a factor moves a position when its up or down change is not
 * zero. The parts of a run merge by factor in the order they were run; a factor two parts both state is one factor, kept once when they
 * state the same results and refused when they do not. A position the run leaves out has no cell.
 */
export function lensCells(reports:readonly RiskReport[]):Map<string,LensCell>{
  const cells=new Map<string,LensCell>(),stated=new Map<string,string>();
  for(const report of reports)for(const item of report.factors){
    const results=canonical(item),earlier=stated.get(item.factor.id);
    if(earlier!==undefined){if(earlier!==results)throw Error('The parts of this run state one factor twice with different results, so its results are not shown. Run it again.');continue;}
    stated.set(item.factor.id,results);
    for(const position of item.positions){
      const key=positionKey(position),cell=cells.get(key)??{tradeReference:position.tradeReference,shocks:[]},v=position.values;cells.set(key,cell);
      if(signOf(v.upPnl)==='zero'&&signOf(v.downPnl)==='zero')continue;
      cell.shocks.push({factor:item.factor.id,label:item.factor.label,bump:item.bump,unit:item.factor.unit,up:v.upPnl,down:v.downPnl,delta:v.delta,deltaUnit:v.deltaUnit,
        gamma:v.gamma,gammaUnit:v.gammaUnit,base:v.base,upValue:v.up,downValue:v.down,measureLabel:position.measureLabel,currency:v.currency,currencyKey:canonical(v.currencyReference)});
    }
  }
  return cells;
}

/** A move: one shock's change of a position, up or down, as the run states it. */
export type Move={positionKey:string;tradeReference:string;change:string;direction:'up'|'down';shock:LensShock};
const magnitude=(value:string)=>value.replace(/^-/,'');
// The larger move by exact comparison of magnitudes; on a tie, the one met first.
const larger=(a:Move|null,b:Move)=>a===null||compareDecimalStrings(magnitude(b.change),magnitude(a.change))>0?b:a;
const moves=(key:string,cell:LensCell):Move[]=>cell.shocks.flatMap(shock=>(['up','down'] as const).map(direction=>
  ({positionKey:key,tradeReference:cell.tradeReference,change:direction==='up'?shock.up:shock.down,direction,shock})));
/** A position's own largest move: one of its shocks, up or down, picked by exact comparison and never combined; none when nothing moves it. */
export function largestMove(key:string,cell:LensCell):Move|null{return moves(key,cell).reduce<Move|null>(larger,null);}

export type LargestMove=Move&{currency:string};
/**
 * What a run says as a whole: how many factors it shocked, each once across its parts; its largest move in each currency, picked by exact
 * comparison of magnitudes and never compared across currencies; and how many positions it leaves out.
 */
export function runSummary(reports:readonly RiskReport[],excluded:readonly {tradeReference:string;reason:string}[]):{factors:number;largest:LargestMove[];excluded:number}{
  const best=new Map<string,Move>();
  for(const [key,cell] of lensCells(reports))for(const move of moves(key,cell))best.set(move.shock.currencyKey,larger(best.get(move.shock.currencyKey)??null,move)!);
  return {factors:new Set(reports.flatMap(r=>r.factors.map(f=>f.factor.id))).size,excluded:excluded.length,
    largest:[...best.values()].map(move=>({...move,currency:move.shock.currency})).sort((a,b)=>a.currency.localeCompare(b.currency)||byText(a.shock.currencyKey,b.shock.currencyKey))};
}

/**
 * The positions a run leaves out because its close cannot value them: every part states them, and every part, made at one close over
 * the same books, the same ones. A part that does not say, or parts that disagree, are refused, never read as leaving out none.
 */
export function runExclusions(reports:readonly RiskReport[]):ExcludedPosition[]{
  if(!reports.length)throw Error('A run with no results states no positions it leaves out.');
  if(reports.some(r=>!Array.isArray(r.excludedPositions)))throw Error('A result of this run does not say which positions it leaves out, so its results are not shown. Run it again.');
  const lists=reports.map(r=>r.excludedPositions!);
  if(new Set(lists.map(list=>JSON.stringify(list))).size>1)throw Error('The parts of this run leave out different positions, so its results are not shown. Run it again.');
  return lists[0];
}

/** The largest magnitude among the changes the lens draws as bars, by currency, for one bar scale: chosen by exact comparison, never computed. */
export function changeScales(cells:ReadonlyMap<string,LensCell>):Map<string,string>{
  const scales=new Map<string,string>();
  for(const cell of cells.values()){
    if(cell.shocks.length!==1)continue;
    const [shock]=cell.shocks;
    for(const change of [shock.up,shock.down]){const top=scales.get(shock.currencyKey);if(top===undefined||compareDecimalStrings(magnitude(change),top)>0)scales.set(shock.currencyKey,magnitude(change));}
  }
  return scales;
}

/** A position row as the change chart draws it: its key, reference, market and commodity. */
export type ChangeRow={key:string;reference:string;market:string;commodity:string};
/**
 * The change if each contract moves up, which the long and short chart becomes after a run: one horizontal panel per currency, with a bar
 * for each position one shock moves at its up change, gains right and losses left, largest gain first. A position several shocks move
 * has no one change, so it has no bar and its panel names it; a position no shock moves is named for the card; one the run leaves out
 * has no cell and is not drawn. `text` is the display text of an exact amount of the position at a row.
 */
export function changeChart(rows:readonly ChangeRow[],cells:ReadonlyMap<string,LensCell>,colors:ReadonlyMap<string,string>,text:(index:number,value:string)=>string):{panels:ChartSpec[];unmoved:string[]}{
  const panels=new Map<string,{currency:string;points:ChartPoint[];several:string[]}>(),unmoved:string[]=[];
  rows.forEach((row,index)=>{
    const cell=cells.get(row.key);
    if(!cell)return;
    if(!cell.shocks.length){unmoved.push(row.reference);return;}
    const [first]=cell.shocks,panel=panels.get(first.currencyKey)??{currency:first.currency,points:[],several:[]};panels.set(first.currencyKey,panel);
    if(cell.shocks.length>1){panel.several.push(`${row.reference} · ${plural(cell.shocks.length,'shock')}`);return;}
    panel.points.push({x:row.key,label:`${row.reference} · ${row.market}`,y:first.up,marker:colors.get(row.commodity),text:signedText(first.up,value=>text(index,value))});
  });
  const specs=[...panels.values()].sort((a,b)=>a.currency.localeCompare(b.currency)).map(panel=>({
    title:`${panel.currency} · bar right = gain`,...(panel.several.length?{subtitle:`Not drawn: ${panel.several.sort(byText).join('; ')}`}:{}),axis:'category',categoryTitle:'Position',unit:panel.currency,
    kind:'bar',orientation:'horizontal',colorBySign:true,
    series:[{name:'Up change',points:panel.points.sort((a,b)=>compareDecimalStrings(b.y!,a.y!)||a.label!.localeCompare(b.label!))}]} satisfies ChartSpec));
  return {panels:specs,unmoved:unmoved.sort(byText)};
}

/** A note of the run bar: what it says beside the run, a part that failed or has no result, or why the results are not shown. */
export type RunNote={kind:'note'|'failed'|'missing'|'alert';text:string};
/**
 * Where a run's results stand on the desk: none yet; shown in the positions, or in a pivot view, which has no lens; refused, with why;
 * or made at another market context than the read on screen, against whose positions they are never shown.
 */
export type RunResults={state:'none'|'elsewhere'}|{state:'shown'|'pivot';excluded:readonly ExcludedPosition[]}|{state:'failed';reason:string};
const reportNouns={held:'held contract',parallel:'held curve',custom:'shock'} as const;
/**
 * What the run bar says of a run (blotter-desk.md, Risk report inline): the report and its sizes; how many it shocked, at which close,
 * knowledge and time; and its notes. `knowledge` is the mode of the read on screen, which a run made now was made at; a retained run
 * states the knowledge it was captured at. A download needs every part's results, and is not offered for results that are refused.
 */
export function runBar(run:RunView,o:{zone:string;knowledge:string|null;results:RunResults}){
  const instant=(value:string)=>displayInstant(value,o.zone),total=run.parts.length,reports=run.parts.flatMap(p=>p.report?[p.report]:[]);
  const count=run.factors??new Set(reports.flatMap(r=>r.factors.map(f=>f.factor.id))).size;
  const knowledge=run.retained||o.knowledge===null?`known at ${run.context?.knownAt?instant(run.context.knownAt):'an unstated time'}`:o.knowledge;
  const finished=run.parts.flatMap(p=>p.job?.finishedAt?[p.job.finishedAt]:[]).sort().at(-1);
  let when:string;
  if(!run.queued)when=`ran ${run.finishedAt?instant(run.finishedAt):'at an unstated time'}${total>1?` · ${total} parts`:''}`;
  else{
    const jobs=total===1?'queued as a job':`queued as ${total} jobs`,states=new Map<string,number>();
    for(const part of run.parts){const state=part.unknown?'outcome unknown':part.job?part.job.state.replaceAll('_',' '):part.failure!==undefined?'not queued':'not yet queued';states.set(state,(states.get(state)??0)+1);}
    when=states.size===1&&states.has('completed')?`${jobs}, completed ${finished?instant(finished):'at an unstated time'}`:`${jobs}: ${[...states].map(([state,n])=>`${n} ${state}`).join(', ')}`;
  }
  const notes:RunNote[]=[];
  const depth=reports[0]?.scenario.depth??0;
  if(depth>0)notes.push({kind:'note',text:`Around ${depth} nested ${depth===1?'stress':'stresses'}: the base, up and down values all include them.`});
  if((o.results.state==='shown'||o.results.state==='pivot')&&o.results.excluded.length){
    const left=o.results.excluded;
    notes.push({kind:'note',text:`Leaves out ${plural(left.length,'position')} this close cannot value: ${left.map(e=>`${e.tradeReference} (${valuationReason(e.reason,e.instrumentType)})`).join('; ')}.`});
  }
  run.parts.forEach((part,index)=>{
    const name=total===1?'The run':`Part ${index+1} of ${total}`;
    if(part.failure!==undefined)notes.push({kind:'failed',text:`${name} failed: ${part.failure}`});
    else if(!part.report&&part.unknown)notes.push({kind:'missing',text:`${name} has no results: its submission has no known outcome.`});
    else if(!part.report&&!part.job)notes.push({kind:'missing',text:`${name} has no results: it is not yet queued.`});
  });
  if(o.results.state==='failed')notes.push({kind:'alert',text:o.results.reason});
  if(o.results.state==='elsewhere')notes.push({kind:'note',text:'This run was made at another market context than the Blotter read on screen, so its results are not shown in the positions. Export downloads them.'});
  // Changing Display reads the Blotter again, which clears the run: a pivot's note points to the download, and to a run made on trade rows.
  if(o.results.state==='pivot')notes.push({kind:'note',text:"Risk results are by position, which totals do not show. Export downloads them for every factor; a run made with Display set to Individual trades shows each position's shocks in its row and in the Risk tab of its panel."});
  const noun=run.setup?reportNouns[run.setup.report]:'shock',complete=total>0&&run.parts.every(p=>p.report);
  return {title:`Risk report · ${run.setupText}`,detail:`${plural(count,noun)} · ${run.asOf?instant(run.asOf):'an unstated'} close · ${knowledge} · ${when}`,notes,
    exportable:complete&&o.results.state!=='failed',exportShown:o.results.state!=='failed',exportNote:complete||o.results.state==='failed'?null:'The download needs the results of every part of the run.'};
}
