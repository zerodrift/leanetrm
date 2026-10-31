// The Latest levels widget: a tile for each configured curve with the front contract, price and unit the backend's curve shapes state at
// the session's close. Nothing here picks a front, prices or judges freshness; it presents the backend's strings and the close's badge.
import {curvesPerRead,figures} from './market-board.js';
import {curveName,deliveryLabel} from './market-view.js';
import {drawSparkline} from './mini-chart-view.js';
import {kpi,node} from './tiles.js';
import {freshnessFooter,type Freshness} from './freshness.js';
import type {CloseBadge} from './latest-close.js';
import type {CurveShape,Sparkline} from './desk-model.js';
import type {MarketNavigation} from './market-navigation.js';

/** What a market widget reads in: the navigation of the context, the session's close (its instant, its own label, its badge) and the zone it shows times in. */
export type DashboardMarket={navigation:MarketNavigation;snapshotId:string;zone:string;historical:boolean;close:{at:string;label:string|null;badge:CloseBadge|null}};
export type LevelTile={curve:string;name:string;front:string;price:string;unit:string;sparkline:Extract<Sparkline,{state:'drawn'}>}|{curve:string;refusal:string};

/** The curves a curve-shapes read can price: the navigation's, without interest-rate curves, which that read does not state. */
export const readableCurves=(navigation:Pick<MarketNavigation,'curves'>)=>navigation.curves.filter(c=>c.marketType!=='rates');

export type ContractChoice={contractId:string;label:string};
/** The contracts a curve's shape at the close states, in the backend's order: all the Price chart can be configured with. */
export const contractChoices=(shape:CurveShape):ContractChoice[]=>(shape.points??[]).map(p=>({contractId:p.contractId,label:deliveryLabel(p.deliveryFrom,p.deliveryTo)}));

/**
 * The tiles in configured order. A configured curve the read did not return is a refusal tile, never a gap; so is one whose tile cannot be
 * shown, such as a curve the navigation does not name, with the reason.
 */
export function levelsTiles(shapes:ReadonlyMap<string,CurveShape|string>,configCurves:readonly string[]):LevelTile[]{
  return configCurves.map(curve=>{
    const shape=shapes.get(curve);
    if(shape===undefined)return {curve,refusal:'The curve read did not return this curve.'};
    try{
      const name=curveName(curve),shown=figures(shape,name);
      return shown.state==='priced'?{curve,name,front:shown.front.delivery,price:shown.front.price,unit:shown.unit,sparkline:shown.line}:{curve,refusal:shown.reason};
    }catch(error){
      // The backend's own reason, when it states one, comes before the page's.
      const shown=figures(shape,curve);
      return {curve,refusal:shown.state!=='priced'?shown.reason:error instanceof Error?error.message:'This curve cannot be shown.'};
    }
  });
}

/**
 * The freshness of the close a widget reads at: Latest or Newer available from the close's badge (an earlier close that was selected is
 * Newer available, since a newer one exists), Historical only in a past knowledge context. No close chosen has no freshness to state. The instant is
 * the close's, as the backend states it; its source is the close's own label.
 */
export function levelsFreshness(badge:CloseBadge|'historical',closeAt:string,label?:string|null):Freshness{
  if(badge==='choose')throw Error('Choose a close to read the dashboard at.');
  const state={latest:'latest',newer:'newer_available',selected:'newer_available',historical:'historical'}[badge] as Freshness['state'];
  return {state,observedAt:closeAt,source:label||'Settlement close'};
}
export function closeFreshness(market:DashboardMarket):Freshness{
  const badge=market.historical?'historical':market.close.badge;
  if(badge===null)throw Error('The market calendar is not loaded, so the close cannot be called the latest. Reload the dashboard.');
  return levelsFreshness(badge,market.close.at,market.close.label);
}
// No intraday source is configured for this release, so the widget says so rather than leaving it unstated.
export const levelsFooter=(freshness:Freshness,zone:string)=>`${freshnessFooter(freshness,zone)} · Intraday not configured`;

/** A widget's refusal, in place of its body: the error's own message, announced. */
export function refusalNote(error:unknown){
  const note=node('p','dashboard-refusal',error instanceof Error?error.message:'This widget could not be shown.');note.setAttribute('role','alert');return note;
}

const line={width:96,height:32};
function tileElement(tile:LevelTile){
  const item=node('li','level-tile');item.dataset.curve=tile.curve;
  if('refusal' in tile){item.dataset.state='refused';item.append(node('p','level-tile-name',tile.curve),node('p','level-tile-reason',tile.refusal));return item;}
  const head=node('div','level-tile-figures');
  head.append(kpi(tile.price,tile.unit),drawSparkline(tile.sparkline,line.width,line.height,'currentColor'));
  item.append(node('p','level-tile-name',tile.name),node('p','level-tile-front',`Front · ${tile.front}`),head);
  return item;
}

/** The shapes of curves at the session's close, as the dashboard's one shared curve-shapes read states them. */
export type SharedShapes=Promise<ReadonlyMap<string,CurveShape|string>>;

/**
 * A footer stating the close's freshness, kept current: `freshen` states it again from the market now on screen, as when a newer close
 * becomes known after the widget was drawn. A close that cannot be named is thrown, for the widget to refuse.
 */
export function closeFooter(market:DashboardMarket,text:(freshness:Freshness,zone:string)=>string,className='fine-print'){
  const footer=node('p',className),freshen=(now:DashboardMarket)=>{footer.textContent=text(closeFreshness(now),now.zone);};
  freshen(market);
  return {footer,freshen};
}

/** The widget's body: a tile for every configured curve from the shared read at the session's close, and its freshness footer. */
export function levelsBody(config:{curves:string[]},market:DashboardMarket,shapes:SharedShapes){
  if(config.curves.length>curvesPerRead)throw Error('This widget names more curves than one read allows.');
  const {footer,freshen}=closeFooter(market,levelsFooter,'fine-print level-tiles-footer'),body=node('div','level-tiles'),list=node('ul','level-tile-list');
  body.append(node('p','muted','Reading the latest levels…'));
  void shapes.then(read=>{
    list.append(...levelsTiles(read,config.curves).map(tileElement));
    body.replaceChildren(list,footer);
  }).catch(error=>body.replaceChildren(refusalNote(error)));
  return {body,freshen};
}
