// The Latest levels widget: a tile for each configured curve with the front contract, price and unit the backend's curve shapes state at
// the session's close. Nothing here picks a front, prices or judges freshness; it presents the backend's strings and the close's badge.
import {curvesPerRead,figures} from './market-board.js';
import {readCurveShapes} from './curve-shapes.js';
import {curveName,deliveryLabel} from './market-view.js';
import {drawSparkline} from './mini-chart-view.js';
import {kpi,node} from './tiles.js';
import {freshnessFooter,type Freshness} from './freshness.js';
import type {CloseBadge} from './latest-close.js';
import type {CurveShape,Sparkline} from './desk-model.js';
import type {MarketNavigation} from './market-navigation.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
/** What a market widget reads in: the navigation of the context, the session's close (its instant, its own label, its badge) and the zone it shows times in. */
export type DashboardMarket={navigation:MarketNavigation;snapshotId:string;zone:string;historical:boolean;close:{at:string;label:string|null;badge:CloseBadge|null}};
export type LevelTile={curve:string;name:string;front:string;price:string;unit:string;sparkline:Extract<Sparkline,{state:'drawn'}>}|{curve:string;refusal:string};

/** The curves a curve-shapes read can price: the navigation's, without interest-rate curves, which that read does not state. */
export const readableCurves=(navigation:Pick<MarketNavigation,'curves'>)=>navigation.curves.filter(c=>c.marketType!=='rates');

export type ContractChoice={contractId:string;label:string};
/** The contracts a curve's shape at the close states, in the backend's order: all the Price chart can be configured with. */
export const contractChoices=(shape:CurveShape):ContractChoice[]=>(shape.points??[]).map(p=>({contractId:p.contractId,label:deliveryLabel(p.deliveryFrom,p.deliveryTo)}));

/** The tiles in configured order. A configured curve the read did not return is a refusal tile, never a gap. */
export function levelsTiles(shapes:ReadonlyMap<string,CurveShape|string>,configCurves:readonly string[]):LevelTile[]{
  return configCurves.map(curve=>{
    const shape=shapes.get(curve);
    if(shape===undefined)return {curve,refusal:'The curve read did not return this curve.'};
    const name=curveName(curve),shown=figures(shape,name);
    return shown.state==='priced'?{curve,name,front:shown.front.delivery,price:shown.front.price,unit:shown.unit,sparkline:shown.line}:{curve,refusal:shown.reason};
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

/** The widget's body: tiles read once, for every configured curve, at the session's close. A body replaced meanwhile (a redraw) is left alone. */
export function levelsBody(config:{curves:string[]},market:DashboardMarket,api:Api):HTMLElement{
  if(config.curves.length>curvesPerRead)throw Error('This widget names more curves than one read allows.');
  const freshness=closeFreshness(market),body=node('div','level-tiles'),list=node('ul','level-tile-list');
  body.append(node('p','muted','Reading the latest levels…'));
  void readCurveShapes(api,{snapshotId:market.snapshotId,context:market.navigation.context??undefined},config.curves).then(shapes=>{
    if(!body.isConnected)return;
    list.append(...levelsTiles(shapes,config.curves).map(tileElement));
    body.replaceChildren(list,node('p','fine-print level-tiles-footer',levelsFooter(freshness,market.zone)));
  }).catch(error=>{if(body.isConnected)body.replaceChildren(refusalNote(error));});
  return body;
}
