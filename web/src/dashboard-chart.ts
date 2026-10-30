// The Price chart widget: the price history of one configured contract, drawn by the same time chart as the contract panel from the
// backend's `history` read at the session's close. The contract is named by its exact id, never by its position or delivery label.
import {readCurveShapes} from './curve-shapes.js';
import {historyBasis,historyChart,type ContractHistory,type RevisionPolicy} from './contract-panel.js';
import {closeFreshness,contractChoices,refusalNote,type DashboardMarket} from './dashboard-levels.js';
import {freshnessFooter} from './freshness.js';
import {curveName,displayInstant} from './market-view.js';
import {renderTimeChart,type TimeChart} from './time-chart.js';
import {node} from './tiles.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
// Each close with the prices it was saved with: the policy the contract panel opens on, named here because a dashboard never picks one silently.
const policy:RevisionPolicy='exact_snapshot_pins_v1';

/** The widget's body and what releases its chart. A body replaced meanwhile (a redraw) is left alone. */
export function chartBody(config:{curve:string;contractId:string},market:DashboardMarket,api:Api):{body:HTMLElement;dispose:()=>void}{
  const freshness=closeFreshness(market),body=node('div','price-chart');
  let chart:TimeChart|undefined;
  body.append(node('p','muted','Reading the price history…'));
  void (async()=>{
    const shapes=await readCurveShapes(api,{snapshotId:market.snapshotId,context:market.navigation.context??undefined},[config.curve]);
    const shape=shapes.get(config.curve);
    if(shape===undefined||typeof shape==='string')throw Error(shape??'The curve read did not return this curve.');
    const contract=contractChoices(shape).find(c=>c.contractId===config.contractId);
    if(!contract||shape.unit===null)throw Error('This contract is not in the curve at this close.');
    const read=await api<ContractHistory>('history',{revisionPolicy:policy,snapshotId:market.snapshotId,curve:config.curve,contractId:config.contractId});
    if(!body.isConnected)return;
    const basis=historyBasis(policy,read,value=>displayInstant(value,market.zone)),series=historyChart(read);
    if('conflict' in series){body.replaceChildren(node('p','dashboard-refusal',series.conflict));return;}
    const host=node('div','contract-chart');
    body.replaceChildren(host,node('p','fine-print',basis),node('p','fine-print',freshnessFooter(freshness,market.zone)));
    chart=renderTimeChart(host,{title:`${curveName(config.curve)} · ${contract.label}`,unit:shape.unit,points:series.points,overlays:[],zone:market.zone});
  })().catch(error=>{if(body.isConnected)body.replaceChildren(refusalNote(error));});
  return {body,dispose:()=>chart?.dispose()};
}
