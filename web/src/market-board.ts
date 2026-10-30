// The market board of Curves & prices (markets-desk.md, Curves & prices): with no curve selected, a card for each curve the library
// lists, grouped and ordered as the library groups them, with what the backend's curve shapes at the selected close state of it: its
// unit, its front contract and price, its contract count and its line, or why the close cannot price it. The same read gives the
// library its sparklines. Nothing here counts, picks a front or prices: it presents the backend's strings.
import {availabilityText,commodityColors,curveLine,type CurveShape,type Sparkline} from './desk-model.js';
import {navigationCurveKind,navigationGroups,type MarketNavigation,type NavigationCurve} from './market-navigation.js';
import {curveName,deliveryLabel,marketTypeName} from './market-view.js';
import {drawSparkline} from './mini-chart-view.js';
import {kpi,node} from './tiles.js';
import {kindBadge,type CurveKind} from './curve-kinds.js';

/** The most curves one curve-shapes read names (the backend's limit). */
export const curvesPerRead=64;
type BoardFilters={by:'commodity'|'marketType';commodity:string|null;type:string};
/**
 * What a card shows of its curve at the close: nothing yet, before the board reads it; that its read failed, and why, until a later read
 * succeeds; that a rates curve is read on its own page, as the curve-shapes read has no rates; the front contract and price, count and
 * line of a curve the close prices; or why the backend states it prices none.
 */
export type CardFigures={state:'unread'}|{state:'failed';reason:string}|{state:'rates'}
  |{state:'priced';unit:string;front:{delivery:string;price:string};count:string;line:Extract<Sparkline,{state:'drawn'}>}
  |{state:'unavailable';unit:string|null;count:string|null;reason:string};
/**
 * A card: its curve's code and name, its market type, its commodity and market type together, the backend's curve kind, its commodity
 * color, and its figures.
 */
export type BoardCard={curve:string;name:string;type:string;kind:string;curveKind:CurveKind;commodity:NavigationCurve['category'];color:string|null;figures:CardFigures};
export type BoardGroup={key:string;name:string;cards:BoardCard[]};

const contracts=(count:number)=>`${count} ${count===1?'contract':'contracts'}`;
const unavailable=(reason:string,unit:string|null=null,count:string|null=null):CardFigures=>({state:'unavailable',unit,count,reason});
const inconsistent=unavailable('Curve shape unavailable · the curve read is inconsistent');
function figures(shape:CurveShape|string,name:string):CardFigures{
  if(typeof shape==='string')return {state:'failed',reason:shape};
  const count=shape.contractCount,counted=typeof count==='number'&&Number.isSafeInteger(count)&&count>=0;
  // What the backend states of a curve whose view selects no contract, or whose contracts the close prices none of, in the board's words.
  if(shape.points!==null&&typeof shape.unit==='string'&&counted){
    const say=(availability:keyof typeof availabilityText)=>availabilityText[availability].replace(/^./,first=>first.toUpperCase());
    if(shape.availability==='no_remaining_contracts')return unavailable(say(shape.availability),shape.unit);
    if(shape.availability==='no_priced_contract')return unavailable(say(shape.availability),shape.unit,contracts(count));
  }
  const line=curveLine(shape);
  if(line.state==='unavailable')return unavailable(line.reason);
  // The backend's front is one of the curve's contracts, which its line marks.
  const front=shape.front!,marks=shape.points!.flatMap((p,x)=>p.contractId===front.contractId?[x]:[]);
  if(marks.length!==1||!counted)return inconsistent;
  const delivery=deliveryLabel(front.deliveryFrom,front.deliveryTo);
  return {state:'priced',unit:shape.unit!,front:{delivery,price:front.price},count:contracts(count),
    line:{state:'drawn',points:line.points,marks,title:`${name} · forward curve, ${line.range}\nFront: ${delivery} · ${front.price} ${shape.unit}`}};
}
function card(curve:NavigationCurve,colors:ReadonlyMap<string,string>,shape:CurveShape|string|undefined):BoardCard{
  const name=curveName(curve.code);
  return {curve:curve.code,name,type:marketTypeName(curve.marketType),kind:navigationCurveKind(curve),curveKind:curve.kind,commodity:curve.category,color:curve.category?colors.get(curve.category.code)!:null,
    figures:curve.marketType==='rates'?{state:'rates'}:shape===undefined?{state:'unread'}:figures(shape,name)};
}
/** The board's cards, in the library's groups and order under the library's filters, from the shapes read so far at the close. */
export function marketBoard(navigation:MarketNavigation,filters:BoardFilters,shapes:ReadonlyMap<string,CurveShape|string>):BoardGroup[]{
  // Colors follow every commodity of the directory, so that a filter never changes a commodity's color.
  const colors=commodityColors(navigation.curves.flatMap(c=>c.category?[c.category.code]:[]));
  return navigationGroups(navigation,filters.by,filters.commodity,filters.type).map(({key,name,curves})=>({key,name,cards:curves.map(c=>card(c,colors,shapes.get(c.code)))}));
}
/**
 * The curve-shapes reads the board still needs: each listed curve with no shape the backend stated, because it is not read yet or its read
 * failed, in the library's order, at most 64 to a read.
 */
export function shapeReads(groups:readonly BoardGroup[]):string[][]{
  const unread=groups.flatMap(g=>g.cards.filter(c=>c.figures.state==='unread'||c.figures.state==='failed').map(c=>c.curve)),reads:string[][]=[];
  for(let i=0;i<unread.length;i+=curvesPerRead)reads.push(unread.slice(i,i+curvesPerRead));
  return reads;
}

/** Commodity identity uses a named shape as well as colour. Rates retain their existing curve-kind icon. */
export function commodityIcon(card:Pick<BoardCard,'commodity'|'color'>){
 if(!card.commodity)return null;
 const code=card.commodity.code,kind=code==='gas'||code==='test_gas'?'gas':code==='electricity'||code==='test_power'?'power':'other';
 const paths={gas:'M10 2c1 4 5 5 5 10a5 5 0 0 1-10 0c0-3 2-5 3-6 0 3 1 4 2 4 2-2 1-5 0-8z',power:'M11 2 4 11h5l-1 7 8-10h-5z',other:'M10 2 18 6v8l-8 4-8-4V6zM2 6l8 4 8-4M10 10v8'};
 const icon=node('i','commodity-icon'),ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg'),path=document.createElementNS(ns,'path');
 icon.dataset.commodityIcon=kind;icon.setAttribute('role','img');icon.setAttribute('aria-label',card.commodity.name);icon.setAttribute('title',card.commodity.name);
 if(card.color)icon.style.setProperty('--commodity',card.color);
 svg.setAttribute('viewBox','0 0 20 20');svg.setAttribute('aria-hidden','true');svg.setAttribute('focusable','false');path.setAttribute('d',paths[kind]);svg.append(path);icon.append(svg);return icon;
}
/** The library row's sparkline: the card's line, small and decorative beside the curve's name; none while the close prices nothing. */
export function librarySparkline(card:BoardCard){
  return card.figures.state==='priced'?drawSparkline(card.figures.line,56,20,card.color??'currentColor',{decorative:true}):null;
}

const cardLine={width:120,height:40};
// A card's foot: the backend's contract count and the curve's price unit, when the read states them.
function foot(count:string|null,unit:string|null){
  const row=node('div','market-card-foot');
  row.append(node('span','market-card-count',count??''),...(unit?[node('span','unit-pill market-card-unit',unit)]:[]));
  return count||unit?[row]:[];
}
function cardBody(card:BoardCard):HTMLElement[]{
  const shown=card.figures;
  switch(shown.state){
    case 'unread':return [node('p','market-card-note','Reading this close…')];
    case 'rates':return [node('p','market-card-note','Zero rates by maturity. Open the curve to see them.')];
    case 'failed':return [node('p','market-card-reason',shown.reason)];
    case 'unavailable':return [node('p','market-card-reason',shown.reason),...foot(shown.count,shown.unit)];
    case 'priced':{
      const figures=node('div','market-card-figures'),front=node('div','market-card-front');
      front.append(node('span','market-card-label',`Front · ${shown.front.delivery}`),kpi(shown.front.price));
      figures.append(front,drawSparkline(shown.line,cardLine.width,cardLine.height,card.color??'currentColor'));
      return [figures,...foot(shown.count,shown.unit)];
    }
  }
}
/**
 * Draws the board's groups into `target`. Each card is a list item whose curve name is the button that opens the curve, and whose whole
 * surface is that button's target; `open` is the library's own way of choosing a curve. While the page is `busy` the buttons are disabled,
 * as the library's rows are.
 */
export function renderBoard(target:HTMLElement,groups:readonly BoardGroup[],open:(curve:string)=>void,busy:boolean){
  if(!groups.length){target.replaceChildren(node('p','market-board-none','No curves match these choices.'));return;}
  target.replaceChildren(...groups.map(group=>{
    const section=node('section','market-board-group'),heading=node('h3','market-board-group-name',group.name),list=node('ul','market-board-grid');
    list.append(...group.cards.map(card=>{
      const item=node('li','market-card'),head=node('div','market-card-head'),title=node('h4','market-card-name'),button=node('button','market-card-open',card.name);
      item.dataset.curve=card.curve;item.dataset.state=card.figures.state;
      button.type='button';button.disabled=busy;button.onclick=()=>open(card.curve);title.append(button);
      const commodity=commodityIcon(card);head.append(...commodity?[commodity]:[],kindBadge(card.curveKind),title);
      item.append(head,node('p','market-card-kind',card.kind),...cardBody(card));
      return item;
    }));
    section.append(heading,list);return section;
  }));
}
