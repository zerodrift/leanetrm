// The contract panel of Curves & prices (markets-desk.md, Contract panel): one contract of the selected curve beside its chart and
// table, with its history over time and how it is priced. The page reads; this module draws what the reads returned, and says which
// revision policy and knowledge each history read used. It chooses no revision, computes no overlay and fills no gap.
import {setupSidePanel,setupTabs,type PanelMode} from './side-panel.js';
import {renderTimeChart,overlayMenu,type OverlayAnswer,type OverlayCatalog,type OverlayRequest,type OverlaySeries,type TimeChart,type TimePoint} from './time-chart.js';
import type {HistoryPoint} from './market-view.js';
import {kpi,node} from './tiles.js';

export type RevisionPolicy='exact_snapshot_pins_v1'|'as_known_at_cutoff_v1';
/** The two revision policies a history read takes (D230), in the order the switch shows them: the panel opens on the first. */
export const revisionPolicies:readonly {policy:RevisionPolicy;label:string}[]=[
  {policy:'exact_snapshot_pins_v1',label:'Exact closes'},
  {policy:'as_known_at_cutoff_v1',label:'As known at cutoff'},
];
/**
 * A `history` read: its points under the policy it names, the knowledge it used, the series it is charted from (one point per instant,
 * or the instant where different samples remain), and the overlays it was asked for, which a conflicting series has none of.
 */
export type ContractHistory={mode:string;revisionPolicy:string;knowledgeCutoff:string|null;knowledgeBasis:string;points:HistoryPoint[];
  chart:{points:{at:string;value:string|null;unavailableReason:string|null}[]}|{conflict:{at:string;message:string}};overlays?:OverlayAnswer[]};

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
 * What the chart draws: the series the read states, a bar per stated point (a point without a price is a gap), or the read's message
 * when different samples remain at one instant. A read that states neither, or both, is refused.
 */
export function historyChart(read:Partial<Pick<ContractHistory,'chart'>>):{points:TimePoint[]}|{conflict:string}{
  const chart=read.chart as {points?:{at:string;value:string|null}[];conflict?:{message?:unknown}}|undefined;
  if(chart&&Array.isArray(chart.points)&&chart.conflict===undefined)return {points:chart.points.map(({at,value})=>({at,value}))};
  if(chart&&chart.points===undefined&&typeof chart.conflict?.message==='string')return {conflict:chart.conflict.message};
  throw Error('The price history does not state the series to chart. Read it again.');
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
  |{state:'read';policy:RevisionPolicy;overlays:number;basis:string;chart:{title:string;unit:string;zone:string;series:{points:TimePoint[]}|{conflict:string};overlays:OverlaySeries[]}};

/**
 * The panel over `#contract-panel`'s markup. `policy` hears the person choose a revision policy, `overlays` the overlays they apply,
 * and `closed` that they closed the panel. The page draws the values table into `#history-values` and the pricing into `pricing()`.
 */
export function setupContractPanel(host:HTMLElement,o:{docked:()=>boolean;sheet:()=>boolean;closed:()=>void;policy:(policy:RevisionPolicy)=>void;overlays:(requests:OverlayRequest[])=>void}){
  const part=(name:string)=>host.querySelector<HTMLElement>(`#${name}`)!;
  // The panel holds no edits, so it never asks before it switches or closes; were it asked, it would keep what it shows.
  const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:async()=>false,docked:o.docked,sheet:o.sheet,closed:()=>{forget();o.closed();}});
  const sections:Record<Tab,HTMLElement>={history:part('contract-history'),pricing:part('contract-pricing')};
  const tabs=setupTabs(shell.element('tabs'),[['history','History'],['pricing','How it’s priced']],{label:'Contract details',panel:shell.element('body'),selected:'history',select:id=>show(id as Tab)});
  function show(tab:Tab){for(const [id,section] of Object.entries(sections))section.hidden=id!==tab;}
  const switcher=part('history-policy'),buttons=new Map(revisionPolicies.map(({policy,label})=>{
    // The page disables every control while it is busy; the policy shown already is not read again.
    const button=node('button',undefined,label);button.type='button';button.onclick=()=>{if(button.getAttribute('aria-pressed')!=='true')o.policy(policy);};return [policy,button] as const;
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
      const series=state.chart.series;
      if('conflict' in series){clearChart(`${series.conflict} Every close is in the values below.${state.overlays?' Overlays cannot be drawn over a series with different prices at one instant.':''}`);return;}
      const spec={title:state.chart.title,unit:state.chart.unit,zone:state.chart.zone,points:series.points,overlays:state.chart.overlays};
      if(chart)chart.update(spec);else chart=renderTimeChart(chartHost,spec);
    },
    pricing:()=>sections.pricing,
    /** Closes the panel without asking, for a page whose curve, close or context changed. */
    reset(){shell.reset();forget();},
  };
}
export type ContractPanel=ReturnType<typeof setupContractPanel>;
