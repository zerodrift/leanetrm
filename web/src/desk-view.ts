// The Blotter desk's DOM: summary tiles, the two market charts, the run bar of a risk run, and the cells and group headers of
// the positions grid, under the Values or the Risk lens. It draws what desk-model and risk-lens selected; every figure is a
// backend string shown in the backend's display format.
import {compareDecimalStrings} from './analytics-model.js';
import {clearChart,renderChart,type ChartSpec} from './chart-view.js';
import {curvePoints,minus,noteMeaning,quantityText,signOf,signedText,type CurveShape,type DeskRow,type Format,type GroupHeader,type PositionsTile,type Sparkline,type Tile} from './desk-model.js';
import {publicText} from './market-view.js';
import {kpi,label,node,space} from './tiles.js';
import {divergingBar} from './mini-charts.js';
import {drawSparkline,svgNode as shape,svgTooltip as tooltip} from './mini-chart-view.js';
import type {LensShock,RunNote} from './risk-lens.js';

// The largest magnitude among exact amounts, for a shared bar scale: chosen by exact comparison, never computed.
function largest(values:readonly string[]){
  let top='0';for(const value of values){const magnitude=value.replace(/^-/,'');if(compareDecimalStrings(magnitude,top)>0)top=magnitude;}return top;
}

function amount(value:string|null,currency:string,money:Format){
  if(value===null)return node('p','kpi-value desk-not-valued','Not valued');
  const figure=kpi(signedText(value,money),currency);figure.dataset.sign=signOf(value);figure.title=`${value} ${currency}`;return figure;
}
function splitBar(value:string|null,max:string){
  const track=shape('svg',{class:'desk-split-bar',viewBox:'0 0 120 10',preserveAspectRatio:'none','aria-hidden':'true'});
  track.append(shape('rect',{x:0,y:4,width:120,height:2,class:'desk-track'}),shape('line',{x1:60,x2:60,y1:0,y2:10,class:'desk-axis'}));
  if(value!==null){const bar=divergingBar(value,max,120);if(bar.width>0)track.append(shape('rect',{x:bar.x,y:1,width:bar.width,height:8,rx:2,class:`desk-bar-${bar.direction}`}));}
  return track;
}
const trades=(count:number,word:string)=>`${count} ${word}${count===1?'':'s'}`;

/** A money tile: per currency its total, a bar and amount per book, and what it leaves out. */
export function renderMoneyTile(host:HTMLElement,title:string,word:string,tile:Tile,money:Format){
  host.replaceChildren(label(title,tile.rows.length===1?trades(tile.rows[0].trades,word):undefined));
  host.classList.toggle('desk-tile-several',tile.rows.length>1);
  for(const row of tile.rows){
    const block=node('div','desk-tile-row');block.dataset.currency=row.currency;
    if(tile.rows.length>1)block.append(node('p','desk-tile-currency',`${row.currency} · ${trades(row.trades,word)}`));
    block.append(amount(row.total,row.currency,money));
    const splits=node('div','desk-splits'),max=largest(row.byBook.flatMap(b=>b.value===null?[]:[b.value]));
    for(const book of row.byBook){
      const split=node('div','desk-split');split.title=publicText(`${book.book} · ${book.value===null?'not valued':`${book.value} ${row.currency}`}${book.excluded?` · excludes ${book.excluded} not valued`:''}`);
      const shown=node('span','desk-split-amount',book.value===null?'Not valued':signedText(book.value,money));if(book.value!==null)shown.dataset.sign=signOf(book.value);
      split.append(node('span','desk-split-book',book.book),space(),splitBar(book.value,max),shown);splits.append(split);
    }
    block.append(splits);
    if(tile.rows.length>1&&row.note)block.append(node('p','desk-tile-note',row.note));
    host.append(block);
  }
  if(tile.note)host.append(node('p','desk-tile-note',tile.note));
}

/** The positions tile: the rows read, and one mix segment per backend commodity and instrument type group. */
export function renderPositionsTile(host:HTMLElement,tile:PositionsTile,colors:ReadonlyMap<string,string>){
  const figure=kpi(String(tile.count),tile.count===1?'position':'positions');
  const bar=node('div','desk-mix'),legend=node('ul','desk-legend');bar.setAttribute('role','img');
  bar.setAttribute('aria-label',publicText(tile.segments.map(s=>`${s.label} ${trades(s.trades,'trade')}`).join(', ')||'No positions'));
  for(const segment of tile.segments){
    const part=node('i',`desk-mix-${segment.instrumentType}`);part.style.flexGrow=String(segment.trades);part.style.background=colors.get(segment.commodity)!;part.title=publicText(`${segment.label} · ${trades(segment.trades,'trade')}`);bar.append(part);
    const item=node('li'),swatch=node('i',`desk-swatch desk-mix-${segment.instrumentType}`);swatch.style.background=colors.get(segment.commodity)!;
    item.append(swatch,node('span',undefined,segment.label),space(),node('b',undefined,String(segment.trades)));legend.append(item);
  }
  host.replaceChildren(label('Open positions'),figure,...(tile.segments.length?[bar,legend]:[]));
  if(tile.note)host.append(node('p','desk-tile-note',tile.note));
}

/** A position's own largest move, as the risk tile draws it in its small bars: the exact change and what the bar's tooltip says. */
export type TileBar={value:string;title:string};
/**
 * A run's results as the risk tile shows them (blotter-desk.md, Desk layout): what it ran, how many shocks it has results for, and per
 * currency its largest move, which position it is and why, and each position's own largest move as a small bar; and what it leaves out.
 */
export type RiskTileResults={label:string;count:string;rows:{currency:string;text:string;exact:string;sign:'positive'|'negative'|'zero';hint:string;bars:TileBar[];scale:string}[];
  empty:string|null;note:string|null};
/**
 * What the risk tile says: no run, its report open, a queued run under way, or a run made elsewhere; a run whose results are refused, or
 * which has none, with why; or a run's results. `denial` is why this account cannot run a risk report, when it cannot.
 */
export type RiskTile={state:'none'|'open'|'running'|'elsewhere';denial:string|null;busy:boolean}|{state:'refused'|'empty';reason:string;denial:string|null;busy:boolean}
  |{state:'results';denial:string|null;busy:boolean;results:RiskTileResults};
const riskWords={none:['Not run at this close','Revalue the books on screen under shared market shocks.','Run risk report'],
  open:['Set up in the panel','The risk report is open in the side panel.','Go to the risk report'],
  running:['Running','A queued risk run is under way. Its results will show here and in the positions.','Go to the risk report'],
  elsewhere:['Results made elsewhere','The run on screen was made at another close, context or books than the Blotter read on screen. Export downloads its results.','Go to the risk report'],
  refused:['Results not shown','','Go to the risk report'],empty:['No results','','Go to the risk report']} as const;
// Each position's own largest move, as one small bar of a strip: up for a gain and down for a loss, on one scale, its sign in its title.
function moveBars(bars:readonly TileBar[],scale:string){
  const width=150,height=34,gap=2,band=Math.max(2,Math.min(8,(width-gap*Math.max(0,bars.length-1))/Math.max(1,bars.length)));
  const strip=shape('svg',{class:'desk-risk-bars',viewBox:`0 0 ${width} ${height}`,width,height,role:'img'});
  strip.setAttribute('aria-label',publicText(`Each position's largest move: ${bars.map(b=>b.title).join('; ')}`));
  strip.append(shape('line',{x1:0,x2:width,y1:height/2,y2:height/2,class:'desk-axis'}));
  bars.forEach((bar,index)=>{
    const geometry=divergingBar(bar.value,scale,height);
    if(geometry.width>0)strip.append(tooltip(shape('rect',{x:index*(band+gap),y:height-geometry.x-geometry.width,width:band,height:geometry.width,rx:1.5,class:`desk-bar-${geometry.direction}`}),bar.title));
  });
  return strip;
}
/**
 * The risk tile: before a run, whether the risk report is open or a queued run is under way, with the way to it; after one, its
 * results. It is drawn once and then updated in place, so a focused button keeps its focus while the page syncs.
 */
export function renderRiskTile(host:HTMLElement,risk:RiskTile,run:()=>void){
  if(!host.firstChild){
    const button=node('button','secondary');button.type='button';button.id='desk-risk-run';
    host.append(node('div','desk-tile-label'),node('p','desk-risk-state'),node('div','desk-risk-moves'),node('p','desk-tile-hint'),node('p','desk-tile-note'),button);
  }
  const part=<T extends Element>(selector:string)=>host.querySelector<T&HTMLElement>(':scope > '+selector)!,button=part<HTMLButtonElement>('button'),moves=part('.desk-risk-moves'),note=part('.desk-tile-note');
  const shown=risk.state==='results'?risk.results:undefined;
  // The tile's name and, after a run, its shock count, read apart as every tile's name and count do.
  part('.desk-tile-label').replaceChildren(node('span',undefined,shown?shown.label:'Risk at this close'),...(shown?[space(),node('span','desk-risk-count',shown.count)]:[]));
  part('.desk-risk-state').hidden=part('.desk-tile-hint').hidden=button.hidden=!!shown;moves.hidden=!shown;
  note.hidden=!shown?.note;note.textContent=shown?.note?publicText(shown.note):'';
  if(risk.state==='results'){
    const rows=risk.results.rows;
    if(risk.results.empty!==null){moves.replaceChildren(node('p','desk-risk-none',risk.results.empty));return;}
    moves.replaceChildren(...rows.map(row=>{
      const block=node('div','desk-tile-row'),figure=kpi(row.text,row.currency);figure.dataset.sign=row.sign;figure.title=`${row.exact} ${row.currency}`;
      if(rows.length>1)block.append(node('p','desk-tile-currency',row.currency));
      block.append(figure,node('p','desk-risk-largest',row.hint),moveBars(row.bars,row.scale));return block;
    }));
    return;
  }
  moves.replaceChildren();
  const [state,hint,action]=riskWords[risk.state],denied=risk.state==='none'&&risk.denial!==null;
  part('.desk-risk-state').textContent=publicText(state);part('.desk-tile-hint').textContent=publicText(denied?risk.denial!:'reason' in risk?risk.reason:hint);
  button.textContent=publicText(action);button.disabled=risk.busy||denied;button.onclick=run;
}

/** What the run bar says, and what its Edit shocks, Export and Clear do. */
export type RunBarView={title:string;setup:string;detail:string;notes:readonly RunNote[];exportable:boolean;exportShown:boolean;exportNote:string|null};
/**
 * The run bar above the tiles (blotter-desk.md, Risk report inline): what ran, with its setup's words, how many it shocked, at which
 * close, knowledge and time, and its notes; Edit shocks, Export and Clear. Nothing while no run is on screen.
 */
export function renderRunBar(host:HTMLElement,bar:RunBarView|null,busy:boolean){
  host.hidden=bar===null;
  if(bar===null)return;
  host.setAttribute('aria-label',publicText(bar.title));
  const setup=host.querySelector<HTMLElement>('#risk-run-setup')!;setup.textContent=publicText(bar.setup);
  host.querySelector<HTMLElement>('#desk-run-detail')!.textContent=publicText(bar.detail);
  host.querySelector<HTMLElement>('#desk-run-notes')!.replaceChildren(...bar.notes.map(n=>{
    const item=node('li',n.kind==='failed'?'risk-part-failed':n.kind==='missing'?'risk-part-missing':n.kind==='alert'?'risk-run-alert':undefined,n.text);
    if(n.kind==='failed'||n.kind==='alert')item.setAttribute('role','alert');return item;
  }));
  const download=host.querySelector<HTMLButtonElement>('#risk-export')!,note=host.querySelector<HTMLElement>('#risk-export-note')!;
  download.hidden=!bar.exportShown;download.disabled=busy||!bar.exportable;note.hidden=bar.exportNote===null;note.textContent=bar.exportNote??'';
  for(const id of ['#desk-run-edit','#desk-run-clear'])host.querySelector<HTMLButtonElement>(id)!.disabled=busy;
}

// A horizontal bar chart pads its plot by 8px above and below the bars (chart-view's grid); each bar takes a band of 22px.
const plotPadding=16,barBand=22;
/** What a bar chart's canvas shows, as text beside it: each bar's label and the text listed with it, in the order drawn. */
function textAlternative(spec:ChartSpec){
  const list=node('ul','sr-only');list.setAttribute('aria-label',publicText(`${spec.title} values`));
  for(const series of spec.series)for(const point of series.points)
    list.append(node('li',undefined,`${spec.series.length>1?`${series.name} · `:''}${point.label??point.x}: ${point.text??point.y??'unavailable'}`));
  return list;
}
/**
 * The panels of one desk chart under its card's heading, each sized to its bars, or a quiet note when there is nothing to chart, and
 * what the card says of all of its panels, such as the positions it leaves out.
 */
export function renderDeskCharts(host:HTMLElement,specs:readonly ChartSpec[],empty:string,notes:readonly string[]=[]){
  clearDeskCharts(host);
  host.append(...notes.map(text=>node('p','desk-chart-note',text)));
  if(!specs.length){host.append(node('p','desk-chart-empty',empty));return;}
  for(const spec of specs){
    const panel=node('div','chart');panel.style.setProperty('--plot-height',`${plotPadding+Math.max(1,...spec.series.map(s=>s.points.length))*barBand}px`);
    host.append(panel);renderChart(panel,spec);panel.append(textAlternative(spec));
  }
}
export function clearDeskCharts(host:HTMLElement){host.querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));host.querySelectorAll('.chart,.desk-chart-empty,.desk-chart-note').forEach(element=>element.remove());}

// How many of a group's positions show, against all of them: row filters may hide some.
const shownOf=(count:number,header:GroupHeader)=>count===header.positions?`${count} ${count===1?'position':'positions'}`:`${count} of ${header.positions} positions`;
/** A position group's header: its name, how many of its positions show, and the backend subtotals of all of them. */
export function groupHeader(header:GroupHeader,count:number,money:Format){
  const element=node('span','desk-group');
  const totals=node('span','desk-group-totals');totals.title='Totals of every position in this group at this close, before row filters';
  const items=header.subtotals.map(subtotal=>{
    const item=node('span','desk-subtotal');item.append(node('span',undefined,subtotal.measure+' '));
    const figure=node('b',undefined,subtotal.amount===null?'not valued':signedText(subtotal.amount,money));if(subtotal.amount!==null){figure.dataset.sign=signOf(subtotal.amount);figure.title=`${subtotal.amount} ${subtotal.currency}`;}
    item.append(figure,node('span',undefined,' '+subtotal.currency+(subtotal.excluded?` · ${subtotal.excluded} not valued`:'')));return item;
  });
  totals.append(...items.flatMap((item,index)=>index?[space(),item]:[item]));
  // Row filters may hide some of the group's positions, while its subtotals are of all of them.
  element.append(node('span','desk-group-label',header.label),node('span','desk-group-count',` · ${shownOf(count,header)}`),space(),totals);
  return element;
}
/**
 * A position group's header under the Risk lens: its name, how many of its positions show and, where the run says so of the group, how
 * many of its positions the run leaves out. Nothing is added across a group's positions or shocks, so it states no figure of them.
 */
export function riskGroupHeader(header:GroupHeader,count:number,leftOut:number){
  const element=node('span','desk-group');element.title=publicText("Each position's results under each shock are its own: nothing is added across positions or shocks");
  element.append(node('span','desk-group-label',header.label),node('span','desk-group-count',` · ${shownOf(count,header)}`),...(leftOut?[node('span','desk-group-left',` · ${leftOut} left out of this run`)]:[]));
  return element;
}

/** The desk's columns in order, each keyed by the trade field a saved layout records its width, order, sort and filter under. */
export const deskColumns=[['tradeReference','Trade'],['market','Market'],['quantity','Position'],['executionPrice','Price'],['mark','Mark'],['presentValue','Value'],['curve','Curve']] as const;
/** The desk's columns under the Risk lens (D222): a position's shock, its up and down changes and its delta, which a saved view never keeps. */
export const riskColumns=[['tradeReference','Trade'],['market','Market'],['quantity','Position'],['riskShock','Shock'],['riskUp','Up'],['riskDown','Down'],['riskDelta','Delta']] as const;
export type CellFormats={money:(index:number,value:string)=>string;quantity:(index:number,value:string)=>string;price:(index:number,value:string)=>string;mark:(index:number,value:string)=>string;factor:(index:number,value:string)=>string};
/**
 * What the Risk lens shows of a row: the shocks that move its position, with the bar scale of its currency; that the run leaves it out,
 * with its row's own reason; that no shock of the run moves it; or, while a part of the run has no results, that it is not known, and why.
 */
export type LensRowView={state:'shocks';shocks:readonly LensShock[];scale:string|undefined}|{state:'left';reason:string}|{state:'unmoved'}|{state:'unknown';reason:string};
// The sizes of a row's shocks, each once, as the Shock cell states them under a count.
const sizesOf=(shocks:readonly LensShock[])=>[...new Set(shocks.map(s=>`±${s.bump} ${s.unit}`))].join('; ');
const sparkWidth=84,sparkHeight=22;
// A row's sparkline from its market's curve shape, or the reason it has none. One row's curve that cannot be drawn says so in its own
// cell; the other rows are unaffected.
function sparklineSvg(shown:CurveShape|string,row:DeskRow,color:string):SVGElement{
  const draw=(sparkline:Sparkline)=>drawSparkline(sparkline,sparkWidth,sparkHeight,color);
  if(typeof shown==='string')return draw({state:'unavailable',reason:shown});
  try{return draw(curvePoints(shown,row));}
  catch{return draw({state:'unavailable',reason:'Curve shape unavailable'});}
}

/**
 * The positions grid's rich cells for the desk columns, by the keys of the grid's columns. Other columns draw their display text.
 * `curve(index)` is the row's curve shape once read, or undefined while it loads; `lens(index)` what the Risk lens shows of the row.
 */
export function deskCells(rows:readonly DeskRow[],colors:ReadonlyMap<string,string>,formats:CellFormats,curve:(index:number)=>CurveShape|string|undefined,
  keys:readonly string[],lens?:(index:number)=>LensRowView){
  // Size bars share a scale per quantity unit and instrument type: a swap's booked notional never dwarfs a future's lots.
  const scaleKey=(row:DeskRow)=>`${row.quantityKey}|${row.instrumentType}`,scale=new Map<string,string>();
  for(const key of new Set(rows.map(scaleKey)))scale.set(key,largest(rows.filter(r=>scaleKey(r)===key).flatMap(r=>r.quantity===null?[]:[r.quantity])));
  const exact=(element:HTMLElement,value:string)=>{element.title=value;return element;};
  return (index:number,column:number):Node|null=>{
    const row=rows[index];if(!row)return null;
    const color=colors.get(row.commodity)!;
    const cell=node('span','desk-cell');
    const risk=keys[column]?.startsWith('risk')?lens?.(index):undefined;
    if(risk)return riskCell(cell,keys[column],risk,row,index);
    switch(keys[column]){
      case 'tradeReference':{const pill=node('span',`desk-pill desk-pill-${row.pill.toLowerCase()}`,row.pill);cell.append(pill,node('span','desk-reference',row.reference));cell.title=publicText(`${row.reference} · revision ${row.revision}`);return cell;}
      case 'market':{const dot=node('i','commodity-dot');dot.style.background=color;dot.title=publicText(row.commodityName);
        const line=node('span','desk-line');line.append(dot,node('span','desk-market',row.market));cell.append(line,node('small','desk-sub',row.delivery));return cell;}
      case 'quantity':{
        cell.classList.add('desk-size');
        const track=shape('svg',{class:'desk-size-bar',width:56,height:12,viewBox:'0 0 56 12','aria-hidden':'true'});track.append(shape('line',{x1:28,x2:28,y1:0,y2:12,class:'desk-axis'}));
        if(row.quantity!==null&&(row.exposure==='long'||row.exposure==='short')){
          // A swap's booked quantity is unsigned; its bar takes the side its backend direction states.
          const bar=divergingBar(row.quantity.replace(/^-/,''),scale.get(scaleKey(row))!,56);
          if(bar.width>0)track.append(shape('rect',{x:row.exposure==='short'?56-bar.x-bar.width:bar.x,y:2,width:bar.width,height:8,rx:2,fill:color}));
        }
        // A future's quantity carries its sign; a swap's booked quantity has none, and its side label states its direction. A
        // scheduled swap has no single quantity, and its cell says why.
        const figure=node('span',row.quantity===null?'desk-figure desk-muted':'desk-figure',quantityText(row,v=>formats.quantity(index,v)));
        figure.title=row.quantity??noteMeaning(row.quantityNote!);
        // The unit and the side or lots sit under the figure, so figures of different units keep their right edges aligned.
        const line=node('span','desk-line'),under=node('small','desk-sub');line.append(track,figure);under.append(node('span','desk-unit',row.unit),` · ${publicText(row.side)}`);cell.append(line,under);return cell;}
      case 'executionPrice':{
        if(row.price===null&&row.priceNote!==null){cell.title=noteMeaning(row.priceNote);cell.append(node('span','desk-muted',row.priceNote),node('small','desk-sub',`${row.priceLabel} · ${row.priceUnit}`));return cell;}
        if(row.price===null){cell.append(node('span','desk-muted','None'),node('small','desk-sub','No fixed price'));return cell;}
        cell.append(exact(node('span','desk-figure',minus(formats.price(index,row.price))),row.price),node('small','desk-sub',`${row.priceLabel} · ${row.priceUnit}`));return cell;}
      case 'mark':{
        // A position the close cannot value leaves its mark blank; its reason is in its Value cell.
        if(row.mark===null){if(row.markNote!==null){cell.title=noteMeaning(row.markNote);cell.append(node('span','desk-muted',row.markNote));}return cell;}
        cell.append(exact(node('span','desk-figure',minus(formats.mark(index,row.mark))),row.mark),node('small','desk-sub',`Mark · ${row.priceUnit}`));return cell;}
      case 'presentValue':{
        if(row.unavailable!==null){cell.classList.add('desk-unvalued');cell.append(node('span','desk-chip','Not valued'),node('small','desk-sub',row.unavailable));return cell;}
        const figure=exact(node('span','desk-figure desk-value',signedText(row.value,v=>formats.money(index,v))),row.value);figure.dataset.sign=signOf(row.value);
        cell.append(figure,node('small','desk-sub',`${row.measure} · ${row.currency}`));return cell;}
      case 'curve':{
        const shown=curve(index);
        if(shown===undefined){const waiting=node('span','sparkline-loading');waiting.setAttribute('role','img');waiting.setAttribute('aria-label','Loading curve shape');return waiting;}
        cell.append(sparklineSvg(shown,row,color));return cell;}
      default:return null;
    }
  };
  // A Risk lens cell: one shock's size, changes and delta; how many shocks move a position several move, whose results are each in the
  // panel and never added; or why the row has none.
  function riskCell(cell:HTMLElement,key:string,risk:LensRowView,row:DeskRow,index:number):Node|null{
    // A line under a figure keeps its start in view, ending in an ellipsis where its column is too narrow, with its whole text as its title.
    cell.classList.add('desk-risk');
    const sub=(text:string)=>{const line=node('small','desk-sub',text);line.title=line.textContent!;return line;};
    if(risk.state==='left'){
      if(key!=='riskShock')return null;
      cell.classList.add('desk-unvalued');cell.title=publicText('This close cannot value this position, so the run leaves it out');
      cell.append(node('span','desk-chip','Left out'),sub(risk.reason));return cell;
    }
    if(risk.state==='unknown'){
      if(key!=='riskShock')return null;
      cell.title=publicText(`Whether this run's shocks move this position is not known: ${risk.reason}`);cell.append(node('span','desk-muted','Not known'),sub(risk.reason));return cell;
    }
    if(risk.state==='unmoved'){
      if(key!=='riskShock')return null;
      cell.title=publicText("None of this run's shocks moves this position");cell.append(node('span','desk-muted','Not moved'),sub("by this run's shocks"));return cell;
    }
    const {shocks}=risk,[shock]=shocks;
    if(shocks.length>1){
      cell.title=publicText(`${shocks.length} shocks move this position: the panel's Risk tab lists each one. Nothing is added across them.`);
      cell.append(node('span','desk-figure desk-muted',`${shocks.length} shocks`),sub(key==='riskShock'?sizesOf(shocks):'Each in the panel'));return cell;
    }
    switch(key){
      case 'riskShock':cell.append(node('span','desk-figure',`±${shock.bump}`),sub(shock.unit));return cell;
      case 'riskUp':case 'riskDown':{
        const change=key==='riskUp'?shock.up:shock.down,track=shape('svg',{class:'desk-size-bar',width:46,height:10,viewBox:'0 0 46 10','aria-hidden':'true'});
        track.append(shape('line',{x1:23,x2:23,y1:0,y2:10,class:'desk-axis'}));
        if(risk.scale!==undefined){const bar=divergingBar(change,risk.scale,46);if(bar.width>0)track.append(shape('rect',{x:bar.x,y:1,width:bar.width,height:8,rx:2,class:`desk-bar-${bar.direction}`}));}
        const figure=node('span','desk-figure desk-value',signedText(change,v=>formats.money(index,v)));figure.dataset.sign=signOf(change);figure.title=change;
        const line=node('span','desk-line');line.append(track,figure);cell.append(line,sub(`${row.measure} · ${shock.currency}`));return cell;}
      default:{
        const figure=node('span','desk-figure',signedText(shock.delta,v=>formats.factor(index,v)));figure.title=shock.delta;
        cell.append(figure,sub(shock.deltaUnit));return cell;}
    }
  }
}
