// The Blotter desk's DOM: summary tiles, the two market charts, and the cells and group headers of the positions
// grid. It draws what desk-model selected; every figure is a backend string shown in the backend's display format.
import {compareDecimalStrings} from './analytics-model.js';
import {clearChart,renderChart,type ChartSpec} from './chart-view.js';
import {curvePoints,minus,noteMeaning,quantityText,signOf,signedText,type CurveShape,type DeskRow,type Format,type GroupHeader,type PositionsTile,type Sparkline,type Tile} from './desk-model.js';
import {publicText} from './market-view.js';
import {divergingBar,sparklineGeometry} from './mini-charts.js';

const svgNamespace='http://www.w3.org/2000/svg';
function node<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
  const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=publicText(text);return element;
}
function shape<K extends keyof SVGElementTagNameMap>(tag:K,attributes:Record<string,string|number>){
  const element=document.createElementNS(svgNamespace,tag);for(const [name,value] of Object.entries(attributes))element.setAttribute(name,String(value));return element;
}
function tooltip(element:SVGElement,text:string){const title=shape('title',{});title.textContent=publicText(text);element.prepend(title);return element;}
// A space between neighbouring pieces of text, so the text a screen reader gets keeps them apart as the layout does.
const space=()=>document.createTextNode(' ');
// The largest magnitude among exact amounts, for a shared bar scale: chosen by exact comparison, never computed.
function largest(values:readonly string[]){
  let top='0';for(const value of values){const magnitude=value.replace(/^-/,'');if(compareDecimalStrings(magnitude,top)>0)top=magnitude;}return top;
}

function label(text:string,aside?:string){const row=node('div','desk-tile-label');row.append(node('span',undefined,text),...(aside?[space(),node('span',undefined,aside)]:[]));return row;}
/**
 * A figure and its unit. The figure is one token that never splits or is cut off (app.css): its length is stated for a desk too
 * narrow to hold it at full size, and the unit may wrap beneath it.
 */
function kpi(figure:string,unit:string){
  const element=node('p','kpi-value'),token=node('span','kpi-figure',figure);
  token.style.setProperty('--figure-length',String(token.textContent!.length));
  element.append(token,space(),node('small',undefined,unit));return element;
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

/** What the risk tile says: `denial` is why this account cannot run a risk report, when it cannot. */
export type RiskTile={state:'none'|'open'|'running'|'results';denial:string|null;busy:boolean};
const riskWords={none:['Not run at this close','Revalue the books on screen under shared market shocks.','Run risk report'],
  open:['Set up in the panel','The risk report is open in the side panel.','Go to the risk report'],
  running:['Running','A queued risk run is under way. Its results will show in the side panel.','Go to the risk report'],
  results:['Results in the panel','Risk results for this close are in the side panel.','Show the results']} as const;
/**
 * The risk tile until risk results come inline: it opens the side panel's risk report and never says not run while results show
 * or a queued run is under way. It is drawn once and then updated in place, so a focused button keeps its focus while the page syncs.
 */
export function renderRiskTile(host:HTMLElement,risk:RiskTile,run:()=>void){
  if(!host.firstChild){const button=node('button','secondary');button.type='button';button.id='desk-risk-run';host.append(label('Risk at this close'),node('p','desk-risk-state'),node('p','desk-tile-hint'),button);}
  const [state,hint,action]=riskWords[risk.state],denied=risk.state==='none'&&risk.denial!==null,button=host.querySelector('button')!;
  host.querySelector('.desk-risk-state')!.textContent=publicText(state);host.querySelector('.desk-tile-hint')!.textContent=publicText(denied?risk.denial!:hint);
  button.textContent=publicText(action);button.disabled=risk.busy||denied;button.onclick=run;
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
/** The panels of one desk chart under its card's heading, each sized to its bars, or a quiet note when there is nothing to chart. */
export function renderDeskCharts(host:HTMLElement,specs:readonly ChartSpec[],empty:string){
  clearDeskCharts(host);
  if(!specs.length){host.append(node('p','desk-chart-empty',empty));return;}
  for(const spec of specs){
    const panel=node('div','chart');panel.style.setProperty('--plot-height',`${plotPadding+Math.max(...spec.series.map(s=>s.points.length))*barBand}px`);
    host.append(panel);renderChart(panel,spec);panel.append(textAlternative(spec));
  }
}
export function clearDeskCharts(host:HTMLElement){host.querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));host.querySelectorAll('.chart,.desk-chart-empty').forEach(element=>element.remove());}

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
  const positions=count===header.positions?`${count} ${count===1?'position':'positions'}`:`${count} of ${header.positions} positions`;
  element.append(node('span','desk-group-label',header.label),node('span','desk-group-count',` · ${positions}`),space(),totals);
  return element;
}

/** The desk's columns in order, each keyed by the trade field a saved layout records its width, order, sort and filter under. */
export const deskColumns=[['tradeReference','Trade'],['market','Market'],['quantity','Position'],['executionPrice','Price'],['mark','Mark'],['presentValue','Value'],['curve','Curve']] as const;
export type CellFormats={money:(index:number,value:string)=>string;quantity:(index:number,value:string)=>string;price:(index:number,value:string)=>string;mark:(index:number,value:string)=>string};
const sparkWidth=84,sparkHeight=22;
// A row's sparkline from its market's curve shape, or the reason it has none.
function sparklineSvg(shown:CurveShape|string,row:DeskRow,color:string):SVGElement{
  const box=shape('svg',{class:'sparkline',width:sparkWidth,height:sparkHeight,viewBox:`0 0 ${sparkWidth} ${sparkHeight}`,role:'img'});
  const unavailable=(reason:string)=>{box.setAttribute('class','sparkline sparkline-unavailable');box.setAttribute('aria-label',publicText(reason));
    box.replaceChildren(shape('line',{x1:3,x2:sparkWidth-3,y1:sparkHeight/2,y2:sparkHeight/2}));return tooltip(box,reason);};
  if(typeof shown==='string')return unavailable(shown);
  // One row's curve that cannot be drawn says so in its own cell; the other rows are unaffected.
  try{
    const sparkline:Sparkline=curvePoints(shown,row);if(sparkline.state==='unavailable')return unavailable(sparkline.reason);
    const geometry=sparklineGeometry(sparkline.points,sparkWidth,sparkHeight);
    if(!geometry.path)return unavailable('Curve shape unavailable · no prices at this close');
    box.append(shape('path',{d:geometry.path,stroke:color,class:'sparkline-line'}));
    for(const x of sparkline.marks){const mark=sparklineGeometry(sparkline.points,sparkWidth,sparkHeight,x).mark;if(mark)box.append(shape('circle',{cx:mark.x,cy:mark.y,r:3,fill:color,class:'sparkline-mark'}));}
    box.setAttribute('aria-label',publicText(sparkline.title.split('\n')[0]));return tooltip(box,sparkline.title);
  }catch{return unavailable('Curve shape unavailable');}
}

/**
 * The positions grid's rich cells for the desk columns. Other columns draw their display text. `curve(index)` is the
 * row's curve shape once read, or undefined while it loads.
 */
export function deskCells(rows:readonly DeskRow[],colors:ReadonlyMap<string,string>,formats:CellFormats,curve:(index:number)=>CurveShape|string|undefined){
  // Size bars share a scale per quantity unit and instrument type: a swap's booked notional never dwarfs a future's lots.
  const scaleKey=(row:DeskRow)=>`${row.quantityKey}|${row.instrumentType}`,scale=new Map<string,string>();
  for(const key of new Set(rows.map(scaleKey)))scale.set(key,largest(rows.filter(r=>scaleKey(r)===key).flatMap(r=>r.quantity===null?[]:[r.quantity])));
  const exact=(element:HTMLElement,value:string)=>{element.title=value;return element;};
  return (index:number,column:number):Node|null=>{
    const row=rows[index];if(!row)return null;
    const color=colors.get(row.commodity)!;
    const cell=node('span','desk-cell');
    switch(deskColumns[column]?.[0]){
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
}
