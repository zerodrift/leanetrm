// The contract panel of Curves & prices (markets-desk.md, Contract panel): one contract of the selected curve beside its chart and
// table, with its history over time and how it is priced. The page reads; this module draws what the reads returned, and says which
// revision policy and knowledge each history read used. It chooses no revision, computes no overlay and fills no gap.
import {askLeave,setupSidePanel,setupTabs,type PanelMode} from './side-panel.js';
import {renderTimeChart,overlayMenu,type OverlayCatalog,type OverlayRequest,type OverlaySeries,type TimeChart,type TimePoint} from './time-chart.js';
import type {HistoryPoint} from './market-view.js';
import {kpi,node} from './tiles.js';

export type RevisionPolicy='exact_snapshot_pins_v1'|'as_known_at_cutoff_v1';
/** The two revision policies a history read takes (D230), in the order the switch shows them: the panel opens on the first. */
export const revisionPolicies:readonly {policy:RevisionPolicy;label:string}[]=[
  {policy:'exact_snapshot_pins_v1',label:'Exact closes'},
  {policy:'as_known_at_cutoff_v1',label:'As known at cutoff'},
];
type OverlayAnswer=Omit<OverlaySeries,'label'>;
/** A `history` read: its points under the policy it names, the knowledge it used, and the overlays it was asked for. */
export type ContractHistory={mode:string;revisionPolicy:string;knowledgeCutoff:string|null;knowledgeBasis:string;points:HistoryPoint[];overlays?:OverlayAnswer[]};

/**
 * What a history read says about the revisions it shows: under exact closes, each close's own saved prices; under the cutoff policy,
 * the cutoff instant it used, or current knowledge in working data. A read under another policy than the one asked for, or whose
 * basis and cutoff disagree, is refused rather than described.
 */
export function historyBasis(asked:RevisionPolicy,read:Pick<ContractHistory,'revisionPolicy'|'knowledgeCutoff'|'knowledgeBasis'>,instant:(value:string)=>string){
  if(read.revisionPolicy!==asked)throw Error('The price history came back under another revision policy than the one chosen. Read it again.');
  const known=read.knowledgeCutoff===null?read.knowledgeBasis==='current_knowledge':read.knowledgeBasis==='selected_context';
  if(!known)throw Error('The price history does not state the knowledge it was read at. Read it again.');
  if(asked==='exact_snapshot_pins_v1')return 'Each close with the prices it was saved with';
  return read.knowledgeCutoff===null?'Latest revisions known now · current knowledge in working data':`Latest revisions known at ${instant(read.knowledgeCutoff)}`;
}

/**
 * The chart's points: one per instant. Closes that saved the same price at one instant are one point; different prices at one instant
 * are not chosen between, and the instant that holds them is returned instead. A point without a price stays a gap.
 */
export function historyChartPoints(points:readonly HistoryPoint[]):{points:TimePoint[]}|{conflict:string}{
  const byInstant=new Map<string,string|null>();
  for(const point of points){
    if(byInstant.has(point.at)&&byInstant.get(point.at)!==point.value)return {conflict:point.at};
    byInstant.set(point.at,point.value);
  }
  return {points:[...byInstant].map(([at,value])=>({at,value}))};
}

const sameParameters=(a:Record<string,number>,b:Record<string,number>)=>{
  const entries=(p:Record<string,number>)=>JSON.stringify(Object.entries(p).sort(([x],[y])=>x<y?-1:1));
  return entries(a)===entries(b);
};
/**
 * The overlays a read returned, each named by its catalog definition, in the order they were asked for. A read whose overlays do not
 * answer the request exactly, under the catalog's policy and with its series, is refused.
 */
export function historyOverlays(requests:readonly OverlayRequest[],answers:readonly OverlayAnswer[]|undefined,catalog:OverlayCatalog):OverlaySeries[]{
  const returned=answers??[];
  if(returned.length!==requests.length||returned.some((answer,i)=>answer.code!==requests[i].code||!sameParameters(answer.parameters,requests[i].parameters)||answer.policy!==catalog.policy))
    throw Error('The chart overlays do not match the ones chosen. Apply them again.');
  return returned.map(answer=>{
    const definition=catalog.overlays.find(d=>d.code===answer.code);
    if(!definition)throw Error('An overlay is not in the overlay catalog. Refresh the page.');
    if(JSON.stringify(Object.keys(answer.series).sort())!==JSON.stringify([...definition.seriesNames].sort()))throw Error('The chart overlays do not match the ones chosen. Apply them again.');
    return {...answer,label:definition.label};
  });
}

/** When a value was known: the latest certification of its prices, not certified yet, or nothing for a close that saved no price. */
export function knownAtText(point:Pick<HistoryPoint,'observationIds'|'knownAt'>,instant:(value:string)=>string){
  if(!point.observationIds.length)return '—';
  return point.knownAt===null?'Not certified yet':instant(point.knownAt);
}

export type ContractMode=Extract<PanelMode,{kind:'contract'}>;
export type ContractHead={kicker:string;title:string;sub:string;figure:string;unit:string};
type Tab='history'|'pricing';
/** What the History tab shows: the policy chosen, and the read's basis and chart once it has arrived, or what is happening instead. */
export type HistoryState=
  |{state:'reading';policy:RevisionPolicy;overlays:number}
  |{state:'failed';policy:RevisionPolicy;overlays:number}
  |{state:'read';policy:RevisionPolicy;overlays:number;basis:string;chart:{title:string;unit:string;zone:string;points:readonly HistoryPoint[];overlays:OverlaySeries[]}};

/**
 * The panel over `#contract-panel`'s markup. `policy` hears the person choose a revision policy, `overlays` the overlays they apply,
 * and `closed` that they closed the panel. The page draws the values table into `#history-values` and the pricing into `pricing()`.
 */
export function setupContractPanel(host:HTMLElement,o:{instant:(value:string)=>string;docked:()=>boolean;sheet:()=>boolean;busy:()=>boolean;closed:()=>void;policy:(policy:RevisionPolicy)=>void;overlays:(requests:OverlayRequest[])=>void}){
  const part=(name:string)=>host.querySelector<HTMLElement>(`#${name}`)!;
  // The panel holds no edits, so it never asks before it switches or closes.
  const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:askLeave(host),docked:o.docked,sheet:o.sheet,closed:()=>{forget();o.closed();}});
  const sections:Record<Tab,HTMLElement>={history:part('contract-history'),pricing:part('contract-pricing')};
  const tabs=setupTabs(shell.element('tabs'),[['history','History'],['pricing','How it’s priced']],{label:'Contract details',panel:shell.element('body'),selected:'history',select:id=>show(id as Tab)});
  function show(tab:Tab){for(const [id,section] of Object.entries(sections))section.hidden=id!==tab;}
  const switcher=part('history-policy'),buttons=new Map(revisionPolicies.map(({policy,label})=>{
    const button=node('button',undefined,label);button.type='button';button.onclick=()=>{if(!o.busy()&&button.getAttribute('aria-pressed')!=='true')o.policy(policy);};return [policy,button] as const;
  }));
  switcher.replaceChildren(...buttons.values());
  const basis=part('history-basis'),chartHost=part('history-chart'),overlaySummary=part('history-overlays').querySelector('summary')!;
  let chart:TimeChart|undefined,menu:OverlayCatalog|undefined;
  function clearChart(message?:string){chart?.dispose();chart=undefined;chartHost.replaceChildren(...(message?[node('p','field-help contract-chart-note',message)]:[]));}
  // A closed panel keeps nothing of what it showed: it opens again on History, with no chart and an empty overlay menu.
  function forget(){clearChart();menu=undefined;part('history-overlay-menu').replaceChildren();part('history-overlays').removeAttribute('open');tabs.mark('history');show('history');}
  return {
    /** Opens the panel on a contract, or gives focus to the one it shows; false when it stayed as it was. */
    open:(mode:ContractMode)=>shell.show(mode),
    current:()=>shell.current() as ContractMode|null,
    place:()=>shell.place(),
    head(head:ContractHead){
      const kicker=node('div','side-panel-kicker'),value=node('div','side-panel-value'),figure=node('div','side-panel-figure');
      kicker.append(node('span','side-panel-reference',head.kicker));
      figure.append(node('span','side-panel-label','Price at this close'),kpi(head.figure,head.unit));value.append(figure);
      shell.element('head').replaceChildren(kicker,node('h2','side-panel-title',head.title),node('p','side-panel-sub',head.sub),value);
    },
    /** The overlay menu for a catalog, drawn once for it: what the person enters stays while the panel shows contracts. */
    catalog(catalog:OverlayCatalog){
      if(menu===catalog)return;menu=catalog;
      // Applying folds the menu away, so the chart it changes is in view; its summary says how many overlays are shown.
      overlayMenu(part('history-overlay-menu'),catalog,requests=>{part('history-overlays').removeAttribute('open');o.overlays(requests);});
    },
    history(state:HistoryState){
      for(const [policy,button] of buttons)button.setAttribute('aria-pressed',String(policy===state.policy));
      overlaySummary.textContent=state.overlays?`Overlays · ${state.overlays} shown`:'Overlays';
      if(state.state!=='read'){basis.textContent=state.state==='reading'?'Reading the history…':'The history could not be read. The message above says why.';clearChart();return;}
      basis.textContent=state.basis;
      const points=historyChartPoints(state.chart.points);
      if('conflict' in points){clearChart(`Several closes saved different prices at ${o.instant(points.conflict)}. The chart draws one price per instant, so it shows none: choose As known at cutoff, or read every close in the values below.`);return;}
      const spec={title:state.chart.title,unit:state.chart.unit,zone:state.chart.zone,points:points.points,overlays:state.chart.overlays};
      if(chart)chart.update(spec);else chart=renderTimeChart(chartHost,spec);
    },
    pricing:()=>sections.pricing,
    /** Closes the panel without asking, for a page whose curve, close or context changed. */
    reset(){shell.reset();forget();},
  };
}
export type ContractPanel=ReturnType<typeof setupContractPanel>;
