// The Price chart widget: the price history of one configured contract, drawn by the same time chart as the contract panel from the
// backend's `history` read at the session's close. The contract is named by its exact id, never by its position or delivery label.
import {historyBasis,historyChart,type ContractHistory,type RevisionPolicy} from './contract-panel.js';
import {closeFooter,contractChoices,refusalNote,type DashboardMarket,type SharedShapes} from './dashboard-levels.js';
import {freshnessFooter} from './freshness.js';
import {curveName,displayInstant} from './market-view.js';
import {renderTimeChart,type TimeChart} from './time-chart.js';
import {node} from './tiles.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
// Each close with the prices it was saved with: the policy the contract panel opens on, named here because a dashboard never picks one silently.
const policy:RevisionPolicy='exact_snapshot_pins_v1';

/** The widget's body from the shared curve read, what states its freshness again, and what releases its chart. */
export function chartBody(config:{curve:string;contractId:string},market:DashboardMarket,shapes:SharedShapes,api:Api){
  const {footer,freshen}=closeFooter(market,freshnessFooter),body=node('div','price-chart');
  let chart:TimeChart|undefined,disposed=false;
  body.append(node('p','muted','Reading the price history…'));
  void (async()=>{
    const shape=(await shapes).get(config.curve);
    if(shape===undefined||typeof shape==='string')throw Error(shape??'The curve read did not return this curve.');
    const contract=contractChoices(shape).find(c=>c.contractId===config.contractId);
    if(!contract||shape.unit===null)throw Error('This contract is not in the curve at this close.');
    const read=await api<ContractHistory>('history',{revisionPolicy:policy,snapshotId:market.snapshotId,curve:config.curve,contractId:config.contractId});
    if(disposed)return;
    const basis=historyBasis(policy,read,value=>displayInstant(value,market.zone)),series=historyChart(read);
    if('conflict' in series){body.replaceChildren(node('p','dashboard-refusal',series.conflict));return;}
    const host=node('div','contract-chart');
    body.replaceChildren(host,node('p','fine-print',basis),footer);
    chart=renderTimeChart(host,{title:`${curveName(config.curve)} · ${contract.label}`,unit:shape.unit,points:series.points,overlays:[],zone:market.zone});
  })().catch(error=>{if(!disposed)body.replaceChildren(refusalNote(error));});
  return {body,freshen,dispose:()=>{disposed=true;chart?.dispose();}};
}
