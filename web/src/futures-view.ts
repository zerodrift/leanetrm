import type {BlotterStreamRequest,BlotterStreamHandlers,BlotterStreamStart} from './blotter-stream.js';
import {refreshAcceptedCrudeOperation,openCrudeOperationPanel} from './crude-operations-model.js';
import {setupCrudeOperations} from './crude-operations.js';
import {readCrudeView} from './crude-trade-view.js';
import {readGasView} from './gas-trade-view.js';
import type {ProductTickets,TicketProduct} from './product-tickets.js';
import {readPpaView} from './ppa-trade-view.js';
import {openPositionWindow,type PositionWindowSelection} from './position-window-selection.js';
import {readPositionFacts,readPositionFactsPage,appendPositionFacts,requirePricedFacts,type PositionFacts} from './position-facts.js';
import {amountText,prepareAmountDisplay} from './amount-display.js';
import {type PartyCatalog,type Trader} from './trade-annotations.js';
import {setupSwapEditor,readSwapView} from './swap-editor.js';
import {setupTradeEditor,readTradeView} from './trade-editor.js';
import {type FuturesContract as Contract} from './futures-contract.js';
import {PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {releasesRetained,retainedText,retryAllowed,isLifecycleCommand,isCrudeOperation,tradeStatusPath} from './trade-requests.js';
import {setupRisk,riskDenied,type RunView} from './risk-view.js';
import {changeChart,changeScales,gapsText,lensCells,madeHere,noMoveText,positionKey,positionMoves,runBar,runExclusions,runGaps,runSummary,shockCount,shocksText,tileWords,unmovedNote,
  type LensCell,type ReadPlace,type RunGap} from './risk-lens.js';
import type {ExcludedPosition,RiskReport} from './risk-report.js';
import {setupFuturesBooking} from './futures-booking.js';
import {draftShapes,otherRevision,readCurveShapes} from './curve-shapes.js';
import {newTradeControl,newTradeProducts,setupNewTrade,type NewProduct} from './new-trade.js';
import {createTableWorkspace} from './table-workspace.js';
import {renderDataGrid} from './data-grid.js';
import {contractPositions,deltaLadder,ladderExport,type LadderCell,type LadderValue} from './delta-ladder.js';
import {pivotMatrix,pivotChartPanels,groupLabel,type PivotGroup} from './analytics-model.js';
import {renderChart,clearChart,type ChartSpec} from './chart-view.js';
import type {BlotterSelection,EntryShown,TradeShown} from './assistant-pages.js';
import {isPriceMeasure,priceMeanPolicy,blotterColumns,blotterDockRules,defaultBlotterArrangement,deskFields,groupChoices,measureChoices,newBlotterView,parseBlotterViews,pivotColumns,readSavedViews,rowGroups,sidebarViews,withoutColumns,type BlotterView,type RowGroup} from './blotter-layout.js';
import {setupDock} from './dock.js';
import {acting,createBlotterOperations,guard,refuse,requireModeColumns,type BlotterCaller,type BlotterOperation} from './blotter-operations.js';
import {rowMenuItems,type RowMenuAction} from './blotter-row-menu.js';
import {openBlotterChannel,type EndReason} from './blotter-channel.js';
import {retainedActions,tieMainWindow,type BlotterContext,type BlotterHeld,type BlotterQuestion} from './blotter-popout.js';
import {setupMenu} from './context-menu.js';
import {closeOutRefusal,closeOutRequest,readCloseOutDraft} from './close-out-draft.js';
import {guardedCsv,downloadCsv} from './csv-export.js';
import {friendly,publicText,displayInstant,deliveryLabel,curveName} from './market-view.js';
import {valuationReason,valuationNote,excludedPositions,valuationMeasure} from './valuation-status.js';
import {commodityColors,deskGroups,rowKey,deskSummaryViews,holdingFormats,positionChartPanels,positionRows,positionsTile,priceUnit,projectsFromSeveralCurves,pvChartPanels,severalCurvesShape,signOf,signedText,staleStripItems,summaryStripItems,waitingStripItems,summaryTiles,valueFields,type CurveShape,type DeskRow,type Holding,type StripItem,type StripRisk,type Summary} from './desk-model.js';
import {askInPanel,liveRefreshPlan,riskRefreshPlan,sameMode,type PanelMode,type RiskRefresh} from './side-panel.js';
import {amendRefusal,setupPositionPanel,type PositionInput,type PositionRisk} from './position-panel.js';
import {setupDetailsPane} from './details-pane.js';
import {bookedPosition,followedPosition,positionIndex,requireTradeRows,revisionNote,setupDeskPanel,tradeIndex} from './desk-panel.js';
import {clearDeskCharts,deskCells,deskColumns,groupHeader,renderBookBars,renderDeskCharts,renderPositionMix,renderRunBar,renderSummaryStrip,riskColumns,riskGroupHeader,type LensRowView} from './desk-view.js';

type Context={contextId:string;effectiveOn:string;knownAt:string};
type ReadRequest={snapshotId:string;convention:string;knowledgeMode:'current'|'retained';contextId?:string};
type BookingCatalog='directory'|'priced_contracts';
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string;portfolio_id:string}[];context:Context;asOf:string;canBook:boolean;canAmendTrades:boolean;bookingUnavailableReason:string|null}&({kind:'directory'}|{kind:'priced_contracts';contracts:Contract[]});
type Holdings={convention:string;pivotNumericalPolicy:string;groups:PivotGroup[];context:Context;asOf:string;evaluatedContracts:number;hasSwaps:boolean;holdings:Holding[];totals:{book:string;currency:string;executionPnl:string|null;unvaluedPositions:number}[];unvaluedPositions:number};
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
// A stored view an earlier release saved is listed with this label. It cannot be applied, and choosing it says why.
const earlierFormatLabel='Earlier format · save again';
const earlierFormatNote=(name:string)=>`Saved view "${name}" is in an earlier format and cannot be applied. Set up the Blotter as you want it and save it again under this name, or delete it.`;
const curveColumn=deskColumns.findIndex(([key])=>key==='curve');
// The trade rows' own columns under both lenses: one grid holds them all, and the lens shows its own (D222). The Values lens shows the
// price, mark, value and curve; the Risk lens the shock, its changes and the delta in their place.
const tradeHead=[...deskColumns,...riskColumns.filter(([key])=>!deskColumns.some(([desk])=>desk===key))];
const riskOnly=new Set<string>(riskColumns.flatMap(([key])=>deskColumns.some(([desk])=>desk===key)?[]:[key]));
const valuesOnly=new Set<string>(deskColumns.flatMap(([key])=>riskColumns.some(([risk])=>risk===key)?[]:[key]));
// The Risk lens columns, which a saved view never keeps.
const riskKeys=riskColumns.flatMap(([key])=>key.startsWith('risk')?[key]:[]);
// What a new read of the Blotter says of the risk run it clears (D222): changing the close, the context or the book scope says so in the
// design's words; any other new read, a Refresh, another Display or pivot, or the Blotter opened again, says it was read again, each
// read at current knowledge being made at a context of its own.
const readNotes={inputs:'Risk results cleared · the close or books changed',read:'Risk results cleared · the Blotter was read again',away:'Risk results cleared · the Blotter opened in a window of its own'} as const;
type ReadCause=keyof typeof readNotes;
// The knowledge a read is made at, as the run bar says a run made on it was: working data, a historical view or live corrections.
const knowledgeWords={working:'current knowledge',historical:'historical view',live:'live corrections'} as const;
// What the risk tile calls the report a run was set up as; a retained run does not record one.
const tileReports={held:'every held contract',parallel:'every held curve',custom:'custom scenario'} as const;
/**
 * A run's results as the desk shows them inline (D222), by the key of the results they are: made at another close, context or books than
 * the read on screen, refused with why, or shown, with each position's cell, what the run leaves out, its summary and the parts that have
 * no results.
 */
type Inline={key:string;state:'elsewhere'}|{key:string;state:'failed';reason:string}
  |{key:string;state:'shown';run:number;cells:Map<string,LensCell>;excluded:ExcludedPosition[];summary:ReturnType<typeof runSummary>;scales:Map<string,string>;gaps:RunGap[]};
// How the Group by control names each row grouping; a view without one is a flat list.
const rowGroupLabels:Readonly<Record<NonNullable<RowGroup>,string>>={book:'Book',market:'Market',instrumentType:'Instrument type'};
const rowGroupLabel=(group:RowGroup)=>group===null?'None':rowGroupLabels[group];
// The trade fields an export of trade rows states, beside the desk's own: money is in its currency.
const numericFields=['tradeCount','lots','quantity','executionPrice','mark','executionPnl','fixedPrice','presentValue','revision'];
// At most this many curves are shaped in one read (market/curve-shapes).
const curvesPerShapeRead=64;

export function setupFutures(options:{tickets:ProductTickets;api:Api;stream:(body:BlotterStreamRequest,handlers:BlotterStreamHandlers,signal:AbortSignal)=>Promise<void>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRunRisk:()=>boolean;bookingUnavailableReason:()=>string;zone:()=>string;profile:()=>string;onReadContext:(at:string,known:string)=>void;
  // Says why work that does not run under act failed: a read in the background, or an action the person took that was refused; and
  // takes that away again once the person acts anew.
  failed:(error:unknown)=>void;failureCleared:()=>void;notice:(message:string)=>void;
  // The definition revision of the curves the selected close prices; positions of another revision get no shape from it.
  definitionRevision:()=>string;
  // The saved views the person lists in the sidebar, and the one the Blotter shows, whenever the saved views are read or the shown one changes.
  onViews:(listed:string[],shown:string|null)=>void;
  // Whether Curves & prices lists a market, and opens it with one of its contracts' price history shown when a contract is named.
  curveListed:(curve:string)=>boolean;openCurve:(curve:string,contractId:string|null)=>Promise<void>;source:()=>{convention:string;snapshotId:string;context?:{contextId?:string}};
  // What the page reports of itself has changed: the panel moved to another mode, or its form was closed, or the lens changed.
  reported:()=>void;
  // The knowledge the Blotter is read at: working data at current knowledge, a historical view, or live corrections.
  knowledge:()=>keyof typeof knowledgeWords;
  // The panel's form started or stopped holding unsaved edits.
  unsavedChanged:()=>void;
  // Whether the page is too narrow to show the panes side by side or above one another.
  narrow:()=>boolean;
  // The page assistant, when the account may use it: a row's menu asks it about a position, which the details then show it.
  assistant:{available:()=>boolean;about:(position:{key:string;reference:string;bookIds:string[];portfolioIds:string[]})=>void};
  // The window the Blotter is in (blotter-workbench.md, Pop-out Blotter): the main window, which pops it out into a window of its own that
  // `open` opens and brings it back; or that window, which `close`s to bring it back.
  // `example` is the example both windows read, which a position's own window reads too; a window that cannot say opens no position window.
  window:({kind:'main';open:(url:string,target:string,features:string)=>Window|null}|{kind:'popout';close:()=>void})&{example:'baseline'|'exchange'|undefined}}){
  const {api,act,busy}=options;
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
  const workspace=createTableWorkspace(get('blotter-workspace'),{
    key:'blotter',rowMode:'trades',rowLabel:'Individual trades',dimensions:groupChoices,measures:measureChoices,
    pivotHelp:'Totals keep instrument types, quantity specifications and currencies separate. Blank measures are unavailable. Lot totals require dated-contract grouping. Price measures keep quote multipliers separate; averages use 18 decimal places, ties to even. Changing display mode clears row filters.',
    scopes:[['portfolios','Portfolios'],['books','Books']],actions:[['risk','Run risk'],['refresh','Refresh'],['export','Export'],['new','New trade'],['popout',options.window.kind==='main'?'Pop out':'Bring back']],onApply:()=>asked(()=>load('directory',false,true)),onScope:()=>void perform({kind:'chooseScope',bookIds:selected('blotter-books'),portfolioIds:selected('blotter-portfolios')}),
    note:get('new-trade-availability'),body:get('blotter-dock'),rows:get('blotter-pane-grid'),rowGroups:rowGroups.map(group=>[group,rowGroupLabels[group]] as const),lens:get('desk-lens'),
  });
  // Existing booking controls share the table toolbar.
  get('blotter-refresh').id='futures-refresh';get('blotter-new').id='futures-new';get('blotter-rows').id='desk-positions';
  let catalog:Catalog|undefined,holdings:Holdings|undefined,summaries:Summary[]|undefined,owner='',snapshot='',uncertain=false,generation=0;
  // The first page of the booked positions a read values: its total is the count the status line gives, and the priced read must hold its
  // bindings. Failed valuation can expose these independently read operational facts.
  let facts:PositionFacts|undefined,factsRequest:Record<string,unknown>|undefined,pricingRead:Promise<void>=Promise.resolve(),pricingController:AbortController|undefined;
  const factPageSize=64;
  const crudeFacts=document.createElement('section');crudeFacts.id='crude-operation-facts';get('desk-positions').after(crudeFacts);
  // Rows and totals wait for the complete valuation. Streamed batches update progress only; a failed read has Retry:
  // the valuation, or the count of the positions read beside it.
  type Loading={state:'reading'}|{state:'valuing';total:number;completed?:number}|{state:'failed';read:'count'|'valuation';reason:string};
  let loading:Loading|undefined;
  get('desk-positions').before(get('blotter-progress'),get('blotter-load-status'));
  function showLoad(next:Loading|undefined){
    loading=next;get('blotter-progress').hidden=!next||next.state==='failed';get('blotter-load-status').hidden=!next;get('blotter-retry').hidden=next?.state!=='failed';
    // Count completed valuation work, without displaying an incomplete portfolio.
    text('blotter-load-text',!next?'':next.state==='reading'?'Valuing positions':next.state==='failed'?`${next.read==='count'?'Counting positions failed':'Valuation failed'} · ${next.reason}`:next.completed===undefined?`Valuing ${next.total} positions`:`Valuing positions · ${next.completed} of ${next.total} complete`);
  }
  // The valued rows of the read under way, in the backend's order, as they have arrived.
  let arrived:Holding[]=[];
  // The curve shapes of the current read, once read: by curve, a shape or why it is unavailable; or why none could be read.
  let shapes:{revision:string;curves:Map<string,CurveShape|string>}|{unavailable:string}|undefined;
  // The desk's read of its curves, while it is under way: what Price before booking waits for before it reads a curve of its own.
  let shapesRead:Promise<void>=Promise.resolve();
  const pending=new PendingCommand();
  // Price before booking draws the curve of each market the draft is for: from the desk's own read when it holds the curve, and otherwise
  // from one read of that curve, kept until the desk reads again.
  const drafts=draftShapes({
    desk:async()=>{await shapesRead;return shapes&&'curves' in shapes?shapes:undefined;},
    revision:()=>{try{return options.definitionRevision();}catch{return undefined;}},
    read:async curve=>{
      let source;try{source=options.source();}catch{return 'Curve shape unavailable · the market selection changed';}
      return (await readCurveShapes(api,source,[curve])).get(curve)??'Curve shape unavailable';
    },
  });
  let lastReadRequest:unknown;
  // The close, the context chosen and the book scope of the read on screen, which tell a read at other inputs from one made again; and
  // where the read was made, which a run's results must have been made at to be drawn against it (D222).
  let readInputs:string|undefined,readPlace:ReadPlace|undefined;
  let chartState:{panels:ChartSpec[];request:{context:unknown;view:unknown}}|undefined;
  // What the trade rows' desk drew from the read on screen, which the details draw their position from.
  let drawn:{rows:DeskRow[];colors:ReadonlyMap<string,string>}|undefined;
  let view=newBlotterView(),savedViews:BlotterView[]=[],table:ReturnType<typeof renderDataGrid>|undefined,tableColumns:string[]=[];
  // The trade rows and summary strip of the previous read, kept on screen, stale, while the Blotter is read again or after that read failed.
  // A position is opened only from the read on screen, so these rows open nothing.
  let kept:{strip:StripItem[]|undefined}|undefined,strip:StripItem[]|undefined;
  // The delta ladder (D230) the Ladder lens draws in place of the trade rows, by the value it shows, with what an export of it states.
  let ladderTable:ReturnType<typeof renderDataGrid>|undefined,ladderValue:LadderValue='delta',ladderShown:{headers:string[];rows:string[][];numeric:number[]}|undefined;
  // Columns the current read shows that a saved view never keeps: each pivot group's count of positions it leaves out, and the desk's curve shapes.
  let transientKeys:ReadonlySet<string>=new Set();
  let versions=new Map<string,string>();
  // Stored views an earlier release saved: listed so they can be saved again or deleted, never applied.
  let earlierViews:string[]=[];
  let draftBase:{name:string;version:string}|undefined;
  let viewsLoaded=false;
  // The Values | Risk lens of the trade rows (D222), the run's results it shows once they are prepared, the key of the results being
  // prepared, and the run the lens last turned to Risk for: a run's results turn it once, and the person turns it after that.
  let lens:'values'|'risk'|'ladder'='values',inline:Inline|undefined,preparing:string|undefined,lensFor:number|undefined;
  // Each report of the run on screen by a number of its own, so that results that are new can be told from those already shown.
  const reportIds=new WeakMap<RiskReport,number>();let reports=0;
  // The Blotter's panes (blotter-workbench.md): the summary strip, the positions, details and charts, arranged by the trader and kept in
  // the view. Moving or collapsing a pane draws the grid again where it now stands, never builds it again. The details collapse by their own
  // control, which asks first about edits they hold.
  let setUp=false,retainedOnly=false;
  const dock=setupDock(get('blotter-dock'),{label:'Blotter panes',rules:blotterDockRules,narrow:options.narrow,initial:view.panes,ownCollapse:['details'],
    tabs:[{id:'summary',label:'Summary',body:get('blotter-pane-summary')},{id:'grid',label:'Positions',body:get('blotter-pane-grid')},{id:'details',label:'Details',body:get('blotter-pane-details')},{id:'charts',label:'Charts',body:get('blotter-pane-charts')}],
    changed:arrangement=>{
      // Charts put away to make room for the details are this visit's, never the view's; once brought back by the person they are theirs again.
      if(chartsMadeWay&&!arrangement.collapsed.includes('charts'))chartsMadeWay=false;
      view.panes=chartsMadeWay?{...arrangement,collapsed:arrangement.collapsed.filter(tab=>tab!=='charts')}:arrangement;
      // A divider dragged or a pane moved, put away or brought back changes the room the open details have: it is sized again once drawn.
      requestAnimationFrame(fitRoom);
    },
    // Which panes are open is part of what the page reports, however they came to be open; while the page is set up, the panes it opens
    // are what it shows first, which its first report states.
    opened:()=>{if(setUp)options.reported();},shown:id=>{if(id==='grid')for(const grid of [table,ladderTable])grid?.relayout();}});
  // A page that has just become narrow shows the positions among its tabs, or the details while they show something; one wide again
  // shows the arrangement as it was, with the details expanded while they show something. Focus in the details stays there.
  let narrowed=false;
  function placeDock(){
    const now=options.narrow(),body=get('blotter-pane-details'),focused=body.contains(document.activeElement);
    dock.relayout();
    if(now!==narrowed&&(now||panel.current()!==null))dock.reveal(panel.current()===null?'grid':'details');
    narrowed=now;fitRoom();
    if(focused&&!body.contains(document.activeElement)&&!body.hidden)get('position-details').focus({preventScroll:true});
  }
  // Open details keep room of their own (detailsRoom, and a minimum height in app.css): where the arrangement leaves them less, the Charts pane
  // makes way for as long as they are open, unless the person has put Charts away themselves. The view is never told of it.
  const detailsRoom=240;
  let chartsMadeWay=false;
  function makeRoom(){
    if(chartsMadeWay||options.narrow()||dock.arrangement().collapsed.includes('charts'))return;
    const group=get('blotter-pane-details').closest('.dock-group');
    // Their minimum height holds them at detailsRoom where the arrangement would leave them less, which then comes out of their neighbours.
    const box=group?.getBoundingClientRect();
    if(!box||box.height>detailsRoom+1&&box.bottom<=get('blotter-dock').getBoundingClientRect().bottom+1)return;
    chartsMadeWay=true;dock.collapse('charts');
  }
  function giveRoomBack(){if(!chartsMadeWay)return;chartsMadeWay=false;if(dock.arrangement().collapsed.includes('charts'))dock.reveal('charts');}
  // Where the dock cannot hold the grid's and the open details' own minimums beside the summary strip and the collapsed bars, the grid and
  // the details above one another share what is left evenly, each at least sharedFloor: no pane ever reaches past the dock's foot.
  const sharedFloor=160;
  function fitRoom(){
    const host=get('blotter-dock');host.style.removeProperty('--grid-room');host.style.removeProperty('--details-room');
    if(panel.current()===null||options.narrow())return;
    const grid=get('blotter-pane-grid').closest('.dock-group'),details=get('blotter-pane-details').closest('.dock-group');
    if(!grid||!details)return;
    const foot=host.getBoundingClientRect().bottom-parseFloat(getComputedStyle(host).paddingBottom);
    const over=Math.max(...Array.from(host.querySelectorAll('.dock-group'),g=>g.getBoundingClientRect().bottom))-foot;
    const g=grid.getBoundingClientRect(),d=details.getBoundingClientRect();
    if(over<=1||d.top<g.bottom-1)return;
    const share=`${Math.max(sharedFloor,Math.floor((g.height+d.height-over)/2))}px`;
    host.style.setProperty('--grid-room',share);host.style.setProperty('--details-room',share);
  }
  // A view the Blotter turns to brings its arrangement with it.
  function showPanes(){chartsMadeWay=false;if(JSON.stringify(dock.arrangement())!==JSON.stringify(view.panes))dock.set(view.panes);}
  get('blotter-reset-layout').onclick=()=>void perform({kind:'resetLayout'});
  get('blotter-popout').onclick=()=>void perform({kind:options.window.kind==='main'?'popOut':'bringBack'});
  if(options.window.kind==='main')get('blotter-bring-back').onclick=()=>void perform({kind:'bringBack'});
  // The risk report of the panel's Risk report mode, for the books and context of the read on screen. What it holds outlives the panel's
  // other modes, so its results stay while a position is looked at; a new read of the Blotter clears it.
  const risk=setupRisk({api,act,busy,zone:options.zone,profile:options.profile,canRun:()=>options.canRunRisk()&&!!holdings,onDenied:clear,onChange:riskChanged,edited:options.unsavedChanged,scope:()=>{
    if(!lastReadRequest)throw Error('Load the blotter before running risk.');
    return {context:{...(lastReadRequest as {context:Record<string,unknown>}).context},bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds]};
  }});
  get('blotter-risk').onclick=()=>void openRiskReport();
  // The run bar's Edit shocks opens the risk report with the run's choices, Export downloads its results and inputs, and Clear takes it off the desk.
  get('desk-run-edit').onclick=()=>void openRiskReport();
  get('risk-export').onclick=()=>void act(risk.download);
  get('desk-run-clear').onclick=()=>{if(!busy())risk.clearRun();};
  // The Values | Risk lens of the trade rows (D222): Values shows the positions' values and their long and short sizes, Risk the run's results.
  for(const button of get('desk-lens').querySelectorAll<HTMLButtonElement>('[data-lens]'))
    button.onclick=()=>{const next=button.dataset.lens as typeof lens;if(busy()||next===lens||!inline||inline.state!=='shown')return;lens=next;captureFilters();render();options.reported();};
  // The Ladder lens shows a run's delta, or its up or down P&L, for each shocked factor (D230).
  for(const button of get('desk-ladder-value').querySelectorAll<HTMLButtonElement>('[data-ladder-value]'))
    button.onclick=()=>{const next=button.dataset.ladderValue as LadderValue;if(busy()||next===ladderValue)return;ladderValue=next;render();options.reported();};
  // Shows the lens, with the one chosen, where it applies: the trade rows of a read whose run's results are on screen.
  function syncLens(){
    const control=get('desk-lens');control.hidden=!(inline?.state==='shown'&&view.mode==='trades'&&holdings);
    for(const button of control.querySelectorAll<HTMLButtonElement>('[data-lens]')){button.setAttribute('aria-pressed',String(button.dataset.lens===lens));button.disabled=busy();}
    const values=get('desk-ladder-value');values.hidden=lens!=='ladder';
    for(const button of values.querySelectorAll<HTMLButtonElement>('[data-ladder-value]')){button.setAttribute('aria-pressed',String(button.dataset.ladderValue===ladderValue));button.disabled=busy();}
  }
  function readContext(){const source=options.source();return {snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained' as const:'current' as const,...(source.context?{contextId:source.context.contextId}:{})};}
  // The main window's tie to the Blotter popped out of it (blotter-popout.ts), which follows what the header selects: the close, the
  // knowledge and the account. Its convention is not a selection, and the pop-out reads its own; a selection the header cannot state now,
  // an unapplied context or no close, leaves the pop-out where it is.
  function popoutContext():BlotterContext|undefined{
    let read;try{read=readContext();}catch{return undefined;}
    return {snapshotId:read.snapshotId,knowledgeMode:read.knowledgeMode,contextId:read.contextId??null,profile:options.profile()};
  }
  const tie=options.window.kind==='main'&&options.window.example?tieMainWindow({source:location.href,example:options.window.example,grace:3000,session:()=>crypto.randomUUID(),
    channel:openBlotterChannel,open:options.window.open,context:popoutContext,returned:cameBack,reported:()=>{options.reported();options.unsavedChanged();},ask:askAbout}):undefined;
  const editor=setupTradeEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),leave:()=>panel.leave(),unsavedChanged:options.unsavedChanged,
    queue:async command=>{takeHanded();if(pending.command)throw Error('Reconcile the retained request before submitting another command.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});

  const crudeOperations=setupCrudeOperations({api,act,busy,context:readContext,actor:options.profile,zone:options.zone,onDenied:clear,pending:()=>pending.command,host:()=>panel.element('form'),unsavedChanged:options.unsavedChanged,
    queue:async command=>{takeHanded();if(pending.command)throw Error('Reconcile the retained request first.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});
  const swapEditor=setupSwapEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),leave:()=>panel.leave(),unsavedChanged:options.unsavedChanged,shape:market=>drafts.shape(market),
    ask:question=>detailsPane.ask(question),
    queue:async command=>{takeHanded();if(pending.command)throw Error('Reconcile the retained request first.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});
  // The form of a new future, which the panel hosts as it hosts the forms of the amendments. What it books is kept as the request it was first sent as.
  const booking=setupFuturesBooking({api,act,busy,zone:options.zone,context:readContext,catalog:()=>catalog,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),unsavedChanged:options.unsavedChanged,shape:market=>drafts.shape(market),
    book:async body=>{takeHanded();const command=pending.begin('futures/book',body,undefined,'New futures trade');store();sync();await send(command);}});
  // The details below the positions: a position's curve, terms, risk and history, and the form of its amendment, a new trade or the risk
  // report. Opening a row opens it; Amend puts the amendment's form in the Terms tab, and a new trade has the whole pane, its form beneath a
  // instrument-type choice. Collapsing the details returns focus to the row; on a narrow page the positions are shown again instead (modeChanged).
  const main=document.querySelector('main')!;
  const editing=()=>editor.unsaved()||swapEditor.unsaved()||crudeOperations.unsaved()||booking.unsaved()||options.tickets.unsaved()||risk.edited();
  const detailsPane=setupDetailsPane(get('blotter-pane-details'),{unsaved:editing,busy,step:direction=>void stepPosition(direction),
    collapsed:()=>{if(!deskPanel.closed())focusSelected();},expand:()=>{dock.reveal('details');makeRoom();fitRoom();},changed:modeChanged});
  const panel=detailsPane.shell;
  placeDock();
  // A request whose outcome is not known is kept with its Check status and Retry in a card on the page, above the panes, whatever the details
  // show, so that nothing leaves the person with no way to find out what became of it.
  const retained=get('futures-pending');
  // A PPA or physical gas request whose outcome is not known offers its reconciliation beside the futures and swap request's card.
  const productPending=document.createElement('section');productPending.className='card';productPending.id='product-trade-pending';get('futures-retained').append(productPending);
  function syncProductPending(){
    const pendingProducts=options.tickets.pending();productPending.hidden=pendingProducts.length===0;
    productPending.replaceChildren();
    for(const product of pendingProducts){const button=document.createElement('button');button.type='button';button.className='secondary';button.textContent=`Reconcile ${newTradeProducts.find(([key])=>key===product)![1].replace(/^Physical/,'physical')} request`;button.disabled=busy();button.onclick=()=>void chooseProduct(product,true);productPending.append(button);}
  }
  // The head of the panel's New trade mode, which asks the page for the product the person chooses and the page mounts that product's editor.
  const entry=setupNewTrade(panel,{choose:product=>void chooseProduct(product),busy});
  // The forms belong to the panel's mode: when it moves to another, whatever they held is dropped (the person was asked first if they
  // held edits), and the page says what it now reports. A new trade is drawn as its head and what it says until its form is in the panel.
  let formsFor:PanelMode=null;
  function modeChanged(){
    const mode=panel.current();
    if(sameMode(mode,formsFor))return;
    // Set before the forms are reset, which places the panel and so calls this again: the mode is then already the one handled.
    const left=formsFor;formsFor=mode;resetForms();
    if(left?.kind==='risk')risk.shelve();
    if(mode!==null&&mode.kind!=='position')deskPanel.left();
    if(mode?.kind==='new')entry.show(mode,mode.product===null?'choose':'loading');
    if(mode?.kind==='risk')risk.mount(panel);
    if(mode?.kind==='contract')drawContract();
    // Details that close collapse, never staying open empty; on a narrow page, where the panes share one group, they give their place back
    // to the positions. Charts that made way for them come back.
    if(mode===null){if(options.narrow())dock.reveal('grid');else{dock.collapse('details');giveRoomBack();}fitRoom();}
    renderRisk();options.reported();
  }
  // The panes stack in one group on a narrow main area: they are placed again when its width changes; a change of its height only sizes
  // the open details' room again.
  let mainWidth=main.clientWidth;
  new ResizeObserver(()=>{if(main.clientWidth!==mainWidth){mainWidth=main.clientWidth;placeDock();}else fitRoom();}).observe(main);
  const position=setupPositionPanel(panel,{zone:options.zone,busy,canAmend:()=>!!holdings&&!!catalog?.canAmendTrades,curveListed:options.curveListed,context:()=>JSON.stringify(readContext()),onDenied:clear,
    read:async(h,formats)=>{
      const stale=!holdings,context=readContext();
      const read=await(h.instrumentType==='physical_crude'?readCrudeView(api,context,h,()=>void operateCrude(h.tradeId)):h.instrumentType==='physical_gas'?readGasView(api,context,h,{actor:options.profile}):h.instrumentType==='ppa'?readPpaView(api,context,h,catalog!.context.knownAt,()=>void manage('ppa',h.tradeId)):h.instrumentType!=='future'?readSwapView(api,context,h,formats,{busy,act,settle:()=>settle(h)}):readTradeView(api,context,h,formats));
      // Details kept on screen while the Blotter is read again do not transition into a working settlement editor.
      return stale?{...read,view:zone=>({...read.view(zone),actions:undefined})}:read;
    },
    amendment:{
      open:h=>void perform({kind:'amend',key:rowKey(h)}),
      // A future's form is open for its binding, a swap's for its instrument.
      shown:h=>{const future=editor.shown(),swap=swapEditor.shown();return !!future&&future.tradeId===h.tradeId&&future.bindingId===h.bindingId||!!swap&&swap.tradeId===h.tradeId&&swap.contractId===h.contractId;},
      close:async()=>{await leave();},
    },
    openCurve:(curve,contractId)=>asked(()=>options.openCurve(curve,contractId))});
  const panelHoldings=()=>holdings?.holdings;
  // The header names the position the details show by its trade and market.
  const titled=(at:PositionInput)=>detailsPane.title(`${at.holding.tradeReference} · ${at.row.market}`);
  const deskPanel=setupDeskPanel({shell:panel,position:{show:(at,tab)=>{position.show(at,tab);titled(at);},update:at=>{position.update(at);titled(at);},clear:position.clear},
    holdings:panelHoldings,input:positionInput,grid:()=>{
    const grid=holdings?table:undefined;if(!grid)return;
    return {selectRow:index=>grid.selectRow(index),focusRow:index=>grid.focusRow(index),revealRow:index=>grid.revealRow(index)};
  },notice:options.notice});
  // Operational facts have their own authorized read; priced rows do not own that form's lifetime.
  function syncPositionDetails(){if(!crudeOperations.shown())deskPanel.sync();}
  // Focus after details that showed no position collapse: the grid's selected row, or else its first row on screen.
  function focusSelected(){
    if(!table||!holdings)return;
    const key=table.selected(),index=key===null?-1:holdings.holdings.findIndex(h=>rowKey(h)===key),at=index>=0?index:table.shownRows()[0];
    if(at!==undefined)table.focusRow(at);
  }
  // While the details show a position, the selection the person moves in the grid shows its position there too, asking first about edits.
  const following=()=>panel.current()?.kind==='position'&&dock.showing('details');
  // ◂ and ▸ show the previous or next position in the grid's order.
  async function stepPosition(direction:-1|1){
    const mode=panel.current();
    if(busy()||!table||!holdings||mode?.kind!=='position')return;
    const at=positionIndex(holdings.holdings,mode),next=at<0?null:table.rowAfter(at,direction);
    if(next!==null)await perform({kind:'selectPosition',key:rowKey(holdings.holdings[next])},{placed:true});
  }
  // Gives the forms the panel hosts up, without asking, and updates the panel's controls for what it then shows.
  // The leave guard must read the final state after every editor has reset.
  // A product ticket closed with a request whose outcome is not known leaves it to be reconciled from the page at once.
  function resetForms(){options.tickets.detach();editor.reset();swapEditor.reset();crudeOperations.reset();booking.reset();risk.dropEdits();panel.place();syncProductPending();syncNewTrade();queueMicrotask(options.unsavedChanged);}
  // Gives the panel's forms up without asking, for a page that is left on the person's word, so that no form stays held that they cannot return to.
  // A new trade has nothing but its form, so the panel it was in is closed with it.
  function dropForms(){resetForms();closeNew();options.reported();}
  function closeNew(){if(panel.current()?.kind==='new')panel.reset();}
  // Asks whether to give up what the panel holds unsaved, when it holds any, and closes its form once the person agrees: false means they
  // kept it. The panel asks for a switch of its own mode; this is the question every other way out of the form asks too.
  async function leave(){if(!await panel.leave())return false;dropForms();return true;}
  // An action of the person's that gives the panel's form up, by reading the Blotter again, which closes the panel, or by leaving it,
  // asks first about the edits the panel holds, before the page is busy: a question asked then would be asked of a page with every
  // control disabled; and is refused, saying so, when the page is or became busy (guard). `kept` hears that the person kept them, for a
  // control that has already changed. An operation's handler hears a refusal, and a failure the page has said (acting); the person's other
  // controls show a refusal, and leave a failure to what said it (asked).
  const guarded=(work:()=>Promise<void>,kept?:()=>void)=>guard({busy,leave,act},work,kept);
  const asked=(work:()=>Promise<void>,kept?:()=>void)=>void guarded(work,kept).catch(error=>{if((error as {shown?:boolean}).shown!==true)options.failed(error);});
  // Leaving the Blotter: no read, form or position of it stays.
  function leavePage(){cancelRead();dropForms();deskPanel.reset();}
  // A trade request whose outcome is not confirmed is owned by the window that sent it, which keeps its Check status and Retry.
  // A main window's unconfirmed requests stay with it; a pop-out's go back with the Blotter (handOver).
  const retainedStays=(move:string)=>{
    if(pending.command||risk.unconfirmed())throw refuse(`a request whose outcome is not confirmed stays with the window that sent it; check its status before the Blotter ${move}`);
  };
  // The view as it stands, its unsaved edits among them, in its saved form: what one window hands the other.
  function handOff(){return parseBlotterViews(JSON.stringify([readView()]))[0];}
  // A view the other window handed over, shown as the person left it there: unsaved.
  function adopt(handed:BlotterView){
    restore();view=structuredClone(handed);draftBase=undefined;table=undefined;showPanes();field('blotter-name').value=view.name;field('blotter-saved').value='';viewControls();
    // A narrow window shows the positions among the stacked panes, as it does once it has become narrow.
    if(options.narrow())dock.reveal('grid');
  }
  // On the Blotter's page the main window shows the Blotter, or, while it is popped out, where it went.
  function placeWindow(){
    const page=get('futures-page'),placeholder=get('blotter-away');
    if(!tie||page.hidden&&placeholder.hidden)return;
    page.hidden=tie.out();placeholder.hidden=!tie.out();
  }
  // The Blotter popped out leaves nothing of itself in the main window.
  function away(){leavePage();replaceRead('away');placeWindow();options.reported();}
  // Work the page takes up once it is idle: the Blotter coming back, while the main window is busy with something else.
  function whenIdle(work:()=>void){if(busy())setTimeout(()=>whenIdle(work),50);else work();}
  // The Blotter is back with the view its window handed back, or the one it took there, which is said; the main window reads it afresh.
  function cameBack(returned:BlotterView,handedBack:boolean,held:BlotterHeld|undefined){
    adopt(returned);const arrived=takeStoredRequest();sync();placeWindow();options.reported();options.unsavedChanged();
    // The pop-out said it held unconfirmed requests: they arrive with it, or the person is told to look for them in its window.
    if(held?.requests&&!arrived)options.notice('The Blotter’s window said it held a request whose outcome is not confirmed, which did not arrive here. Check its status in that window, if it is still open, or in Recent risk jobs and the trade history.');
    const said=()=>{if(!handedBack)options.notice('The Blotter’s window closed without handing back its view, so the Blotter shows the view it had when it was popped out.');};
    if(get('futures-page').hidden){said();return;}
    whenIdle(()=>void act(()=>load('directory',false,true)).then(said));
  }
  // A position the pop-out asks the assistant about, found in the main window's own read of the booked positions it may read: the pop-out
  // names only its key.
  // names its key and the books and portfolios its Blotter shows, whose positions alone are read, as the page's own action.
  function askAbout({key,bookIds,portfolioIds}:BlotterQuestion){
    whenIdle(()=>void act(async()=>{
      const context={...readContext(),convention:'trading_workspace_v1'};
      for(let offset:number|null=0;offset!==null;){
        const page=readPositionFactsPage(await api('trading/positions',{context,bookIds,portfolioIds,offset,limit:factPageSize}),factPageSize,offset);
        const found=page.positions.find(p=>`${p.tradeId}:${p.bindingId}`===key);
        if(found){window.focus();options.assistant.about({key,reference:found.tradeReference,bookIds,portfolioIds});return;}
        offset=page.nextOffset;
      }
      throw Error('The Blotter’s window asked about a position this window cannot read, so nothing was put to the assistant.');
    }));
  }
  // Whether the pop-out holds edits that a change of the main window's context or account would give up: it may, until it has reported. Its
  // unconfirmed requests are never given up, as they go with the Blotter, so they are not asked about.
  const windowEdits=()=>!!tie?.out()&&tie.held()?.edits!==false;
  // Asks, in the main window, before a change gives up the edits the pop-out holds: false means the person kept them. One question at a time.
  let askingWindow=false;
  async function leaveWindow(){
    if(!windowEdits())return true;
    if(askingWindow)return false;
    askingWindow=true;
    try{return await askInPanel(get('blotter-window-question'),{text:'The Blotter’s window holds unsaved edits that this gives up.',keep:'Keep them',go:'Give them up'})(new AbortController().signal);}
    finally{askingWindow=false;}
  }
  // A position opens, in the details or a window of its own, only from the read on screen: rows kept from the previous read, or drawn
  // while the read is under way, say so.
  const notOpened=()=>options.notice('Positions open once the Blotter has valued them.');
  function popout(selected:Holding){
    if(busy())return;
    if(!holdings){notOpened();return;}
    if(editing()||pending.command){options.notice('Finish the current trade edit before opening a separate trade window.');return;}
    if(!snapshot)throw Error('Read the Blotter before opening a separate trade window.');
    const example=options.window.example;if(!example){options.failed(refuse('this window does not know which example it reads, so it opens no trade window'));return;}
    const selection:PositionWindowSelection={snapshotId:snapshot,context:holdings.context,asOf:holdings.asOf,
      tradeId:selected.tradeId,bindingId:selected.bindingId,revision:Number(selected.revision),instrumentType:selected.instrumentType as PositionWindowSelection['instrumentType'],zone:options.zone(),
      example};
    openPositionWindow(location.href,selection,(url,target,features)=>window.open(url,target,features));
  }
  // A swap position's settlement and P&L view (D215) opens from its Terms in the panel's form part, as the form of its amendment does.
  function settle(h:Holding){asked(()=>swapEditor.openSettlement(h));}
  // A row's menu (blotter-workbench.md, Row context menu): each action the account may not take now is shown with why. Rows kept on
  // screen from an earlier read, or drawn while the page is busy, offer nothing, saying why. Escape gives focus back to the row.
  // The body is marked while the Blotter shows, for the layout that fills the window with it (app.css).
  {const page=get('futures-page'),mark=()=>document.body.classList.toggle('blotter-shown',!page.hidden);new MutationObserver(mark).observe(page,{attributes:true,attributeFilter:['hidden']});mark();}
  const rowMenu=setupMenu('blotter-row-menu');get('futures-page').append(rowMenu.element);
  // The position and revision a row's menu was opened on, while the Close out chosen from it runs: an operation names only its position.
  let menuRevision:{key:string;revision:string}|undefined;
  function focusRow(key:string){const index=holdings?.holdings.findIndex(h=>rowKey(h)===key)??-1;if(table&&index>=0)table.focusRow(index);}
  function openRowMenu(h:Holding,at:{x:number;y:number}){
    try{showRowMenu(h,at);}catch(error){options.failed(error);}
  }
  function showRowMenu(h:Holding,at:{x:number;y:number}){
    const key=rowKey(h),control=bookingControl(),amend=amendRefusal(h.instrumentType,!!catalog?.canAmendTrades,position.amendState(h));
    const stale=busy()?'The Blotter is busy; try again when it is idle.':!holdings?'Positions open once the Blotter has valued them.':undefined;
    const can={book:control.enabled,amend:amend===null,ask:options.assistant.available(),
      why:{book:control.reason??undefined,amend:amend??undefined,ask:'Your role does not include the page assistant.'},...(stale===undefined?{}:{stale})};
    rowMenu.open(`Position ${h.tradeReference}`,rowMenuItems({key,instrumentType:h.instrumentType,reference:h.tradeReference},can,action=>void rowAction(h,action)),at,()=>focusRow(key));
  }
  async function rowAction(h:Holding,action:RowMenuAction){
    const key=rowKey(h);
    switch(action.kind){
      // Close out drafts the revision the menu was opened on, which a read landing since then may have replaced.
      case 'draftCloseOut':menuRevision={key,revision:h.revision};try{await perform(action);}finally{menuRevision=undefined;}return;
      case 'openWindow':popout(h);return;
      case 'copyId':
        try{await navigator.clipboard.writeText(h.tradeReference);options.notice(`Trade ID ${h.tradeReference} copied.`);}
        catch(error){options.failed(Error(`The trade ID could not be copied: ${error instanceof Error?error.message:String(error)}`));}
        focusRow(key);return;
      // Amend and Ask act on the position the details show, which they show first, asking about what the details hold.
      case 'amend':if(await perform({kind:'selectPosition',key})===true)await perform(action);return;
      case 'ask':if(await perform({kind:'selectPosition',key})===true)options.assistant.about({key,reference:h.tradeReference,bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds]});return;
      default:await perform(action);
    }
  }
  // Close out (owner, 2026-10-30) opens the backend's draft of the offsetting trade, at the curve price of the read on screen, in the new trade
  // form of its product: a future's after the priced contracts are read, as New trade reads them, a swap's in its ticket. Nothing books
  // until the person presses Book. The draft is of the revision the row's menu was opened on, or the row showed when the operation was
  // dispatched without one: a row that holds another revision by then, or after the read made for the priced contracts, refuses it.
  async function draftCloseOut(key:string){
    const row=()=>{const h=holdings?.holdings.find(h=>rowKey(h)===key);if(!h)throw refuse('that position is not among the rows on screen');return h;};
    const chosen=menuRevision?.key===key?menuRevision.revision:row().revision,before=panel.current();
    const unchanged=(h:Holding)=>{if(h.revision!==chosen)throw refuse(`${h.tradeReference} is now at revision ${h.revision}, not revision ${chosen} that was chosen; choose Close out again to draft the revision shown`);};
    unchanged(row());
    // The page's action says its own failures in the banner; the backend's refusal, said in the details, is the operation's answer.
    let refused:Error|undefined;
    await guarded(async()=>{
      // The draft is read in the context of the read that holds the priced contracts, or not at all.
      const priced=()=>catalog?.kind==='priced_contracts'&&!!holdings;
      if(row().instrumentType==='future'&&!priced()){
        await load('priced_contracts');
        if(!priced())throw refuse('the Blotter could not be read with its priced contracts, so nothing was drafted');
      }
      const h=row(),control=bookingControl();if(!control.enabled)throw Error(control.reason);
      unchanged(h);
      const product=h.instrumentType==='future'?'futures' as const:'swaps' as const;
      let drafted;
      try{drafted=readCloseOutDraft(await api('trading/close-out-draft',closeOutRequest({snapshotId:snapshot,contextId:holdings!.context.contextId},h)),h.instrumentType==='future'?'future':'financial_swap');}
      catch(error){
        // The backend's own refusal is said in the details, in place of a form, and still refuses the operation; any other failure is the page's.
        if((error as {code?:string}).code==='access_denied')clear();
        refused=closeOutRefusal(error,h.tradeReference);if(!refused)throw error;
        await showRefusal(refused);return;
      }
      const shown={reference:h.tradeReference},next:PanelMode={kind:'new',product};
      // A draft a form cannot hold whole leaves no form, and the details as they were: never a part-filled form that could be booked.
      try{
        if(drafted.kind==='future'){await panel.show(next);booking.populate();booking.open();booking.closeOut(drafted,shown);}
        else{resetForms();await panel.show(next);if(!await swapEditor.openCloseOut(drafted,shown))throw Error(`The close-out of ${h.tradeReference} was not opened, as the market selection changed while it was read.`);}
      }catch(error){resetForms();await restoreDetails(before);throw error;}
      showForm();
    });
    if(refused)throw refused;
    const shown=panel.current();if(shown?.kind==='new'&&shown.product!==null)panel.focusForm();
  }
  // A refused draft leaves the details on New trade, saying why in place of a form: nothing was drafted.
  async function showRefusal(refused:Error){
    await panel.show(newTrade);
    const said=document.createElement('p');said.id='close-out-refusal';said.className='side-panel-status';said.setAttribute('role','alert');
    said.textContent=publicText(refused.message);panel.element('view').replaceChildren(said);panel.reveal();
  }
  // The details as they were before a close-out that could not be opened: the position they showed, if it is still on screen, or what else
  // they showed, without a form; or closed.
  async function restoreDetails(before:PanelMode){
    if(before?.kind==='position'){
      const index=holdings?.holdings.findIndex(h=>h.tradeId===before.tradeId&&h.bindingId===before.bindingId)??-1;
      if(index>=0){await deskPanel.open(index);return;}
      panel.reset();return;
    }
    if(before===null){panel.reset();return;}
    await panel.show(before.kind==='new'?newTrade:before);
  }
  // New trade (D221) opens the panel on its instrument-type choice with no product selected, and reads nothing until the person chooses one.
  // It is offered only where the backend allows booking; the toolbar says why when it does not, so no field opens where booking is impossible.
  // What the panel held is left only when the person says so, which is asked before the page is busy.
  const newTrade:PanelMode={kind:'new',product:null};
  const bookingControl=()=>newTradeControl({catalog,retained:!!pending.command||options.tickets.pending().length>0,fallback:options.bookingUnavailableReason()});
  async function openNewTrade(){
    if(busy()||!bookingControl().enabled)return;
    if(await panel.show(panel.current()?.kind==='new'?panel.current():newTrade))panel.reveal();
  }
  // A product's form takes the panel's place, scrolled to so that it has the window's height.
  function showForm(){panel.form(true);panel.reveal();}
  // Where the person is: the account and market selection a read is made for. A product whose read failed leaves them on the choice of a product,
  // but only while they are still where they were.
  const whereAmI=()=>{try{return JSON.stringify([options.profile(),options.source()]);}catch{return undefined;}};
  // Each product opens its form once what it needs has been read, and says whether it did: a swap's reset, or a change of selection while it was read, leaves it closed.
  async function openFuture(next:PanelMode){
    // The priced contracts come with a new read of the whole Blotter, which closes the panel and the forms it held.
    await load('priced_contracts');
    const control=bookingControl();
    if(!control.enabled)throw Error(control.reason);
    await panel.show(next);booking.populate();booking.open();
    return true;
  }
  async function openSwap(next:PanelMode){
    // The person has said what the panel held may go: its forms are given up before the panel moves, which would otherwise ask again of a busy page.
    resetForms();
    await panel.show(next);
    return swapEditor.openNew();
  }
  async function openAdditional(product:TicketProduct,next:PanelMode){
    resetForms();await panel.show(next);await options.tickets.open(product,panel.element('form'));return true;
  }
  async function operateCrude(trade:string){
    if(busy()||!await panel.leave())return;
    const selected=facts?.positions.find(p=>p.tradeId===trade)??holdings?.holdings.find(p=>p.tradeId===trade);
    if(!selected)throw Error('Refresh the booked position before opening delivery and cash.');
    const place=whereAmI();
    await act(async()=>{resetForms();await openCrudeOperationPanel({tradeId:trade,bindingId:selected.bindingId,revision:Number(selected.revision)},next=>panel.show(next),()=>crudeOperations.open(trade),()=>place===whereAmI(),()=>{panel.form(true);panel.reveal();detailsPane.title(`${selected.tradeReference} · delivery and cash`);});});
  }
  async function manage(product:TicketProduct,trade:string){
    if(busy()||!await panel.leave())return;
    await act(async()=>{resetForms();await options.tickets.open(product,panel.element('form'),trade);panel.form(true);panel.reveal();});
  }
  // A product is chosen: its form opens once what it needs has been read, in one action with the question asked first. The panel never holds a
  // product it has nothing to show for: when a product's form could not be opened it asks for a product again, with the failure said, whichever
  // product it was, and a page that is busy leaves the panel as it was.
  async function chooseProduct(product:NewProduct,reconcile=false,inAction=false){
    const next:PanelMode={kind:'new',product};
    if(busy()&&!inAction||sameMode(panel.current(),next))return;
    if(!reconcile&&(pending.command||options.tickets.pending().some(p=>p!==product))){options.notice('Reconcile the retained trade request before starting another.');return;}
    if(!await panel.leave())return;
    const at=whereAmI();let ran=false,opened=false;
    const open=async()=>{
      ran=true;
      try{opened=product==='futures'?await openFuture(next):product==='swaps'?await openSwap(next):await openAdditional(product,next);}
      catch(error){if(at!==undefined&&whereAmI()===at)await panel.show(newTrade);throw error;}
      if(opened)showForm();else if(sameMode(panel.current(),next))await panel.show(newTrade);
    };
    if(inAction)await open();else await act(open);
    // A page that became busy while the person was asked does nothing: the panel is as it was, and the person is told, not left to wonder.
    if(!ran)options.notice(`${newTradeProducts.find(([key])=>key===product)![1]} was not opened because the page was busy. Choose it again.`);
    // The form takes focus once the page is no longer busy, which disables every control of it.
    if(opened&&sameMode(panel.current(),next))panel.focusForm();
  }
  const selected=(id:string)=>Array.from(get<HTMLSelectElement>(id).selectedOptions,o=>o.value);
  function choices(id:string,rows:{id:string;name:string}[],ids:string[]){
    const select=get<HTMLSelectElement>(id);select.replaceChildren(...rows.map(r=>new Option(publicText(r.name),r.id,false,ids.includes(r.id))),...ids.filter(id=>!rows.some(r=>r.id===id)).map(id=>new Option('Unavailable saved selection',id,false,true)));
  }
  function captureFilters(){if(!table)return;const state=table.state();view.query=state.query;view.columnFilters=Object.fromEntries(state.columns.flatMap((value,i)=>value===null||transientKeys.has(tableColumns[i])?[]:[[tableColumns[i],value]]));}
  // A view keeps the layout of its own columns: the Risk lens draws others, whose layout it never keeps.
  function captureLayout(){captureFilters();if(table?.ready())view.grid=withoutColumns(table.layout(),transientKeys);}
  function readView():BlotterView{
    captureLayout();const next=structuredClone(view);next.mode=field('blotter-mode').value as BlotterView['mode'];next.sidebar=get<HTMLInputElement>('blotter-sidebar').checked;
    if(get<HTMLSelectElement>('blotter-books').options.length)next.bookIds=selected('blotter-books');
    if(get<HTMLSelectElement>('blotter-portfolios').options.length)next.portfolioIds=selected('blotter-portfolios');
    next.rowGroup=(field('blotter-row-group').value||null) as RowGroup;
    next.splitBy=Array.from(get('blotter-split-dimensions').querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value);
    next.chart={visible:get<HTMLInputElement>('blotter-show-chart').checked,measure:field('blotter-chart-measure').value,kind:field('blotter-chart-kind').value as BlotterView['chart']['kind']};
    next.groupBy=Array.from(get('blotter-dimensions').querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value).concat(next.splitBy);
    next.measures=Array.from(get('blotter-measures').querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value);
    if(view.mode!==next.mode||JSON.stringify([view.groupBy,view.splitBy,view.measures])!==JSON.stringify([next.groupBy,next.splitBy,next.measures]))relayout(next);
    if(next.mode==='trades'){next.splitBy=[];next.groupBy=[];next.measures=[];}
    return next;
  }

  // A changed display mode, grouping or measure starts its own columns and drops the filters of the last one.
  function relayout(next:BlotterView){next.columns=next.mode==='pivot'?pivotColumns(next.groupBy,next.measures):newBlotterView().columns;next.query='';next.columnFilters={};next.grid={order:[],widths:{},sort:[]};}
  function viewControls(){
    field('blotter-mode').value=view.mode;get('blotter-pivot-fields').hidden=view.mode!=='pivot';
    // Trade rows group by the desk's choice; a pivot groups by its own dimensions and has the configurable chart.
    field('blotter-row-group').value=view.rowGroup??'';get('blotter-row-group-control').hidden=view.mode==='pivot';get('blotter-chart-settings').hidden=view.mode!=='pivot';
    choices('blotter-books',catalog?.books.map(b=>({id:b.id,name:b.name}))??[],view.bookIds);
    choices('blotter-portfolios',Array.from(new Map(catalog?.books.map(b=>[b.portfolio_id,{id:b.portfolio_id,name:b.portfolio}])??[]).values()),view.portfolioIds);
    workspace.pivot(view.groupBy,view.measures,view.splitBy);get<HTMLInputElement>('blotter-show-chart').checked=view.chart.visible;field('blotter-chart-measure').value=view.chart.measure;field('blotter-chart-kind').value=view.chart.kind;
    get('blotter-chart-card').querySelectorAll<HTMLButtonElement>('[data-plot-kind]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.plotKind===view.chart.kind)));
    choose('blotter-saved',[...savedViews.map(v=>({id:v.name,name:v.name})),...earlierViews.map(name=>({id:name,name:`${name} · ${earlierFormatLabel}`}))],field('blotter-saved').value||draftBase?.name||'','Current view · not saved');
    get<HTMLInputElement>('blotter-sidebar').checked=view.sidebar;
    if(viewsLoaded)options.onViews(sidebarViews(savedViews),draftBase?.name??null);
  }
  async function layoutApi<T>(path:string,body?:unknown):Promise<T>{
    try{return await api<T>(path,body);}catch(error){
      if((error as {code?:string}).code==='access_denied'){forgetViews();viewControls();clear();}
      throw error;
    }
  }
  function forgetViews(){savedViews=[];earlierViews=[];versions.clear();viewsLoaded=false;}
  async function loadViews(){
    const saved=await layoutApi<{version:string;layout:{name:string}}[]>('table-views/blotter');
    const read=readSavedViews(saved.map(v=>v.layout));
    savedViews=read.views;earlierViews=read.earlier;versions=new Map(saved.map(v=>[v.layout.name,v.version]));viewsLoaded=true;viewControls();
  }

  // A trade request whose outcome is unconfirmed stays with the window that sent it: the pop-out keeps its own, never the main window's, which
  // its storage holds a copy of from when it opened.
  const requestKey=(popout:boolean)=>`openetrm.futures.request.server_booking_utc_v1.${popout?'blotter-window.':''}${options.profile()}`;
  const storageKey=()=>requestKey(options.window.kind==='popout');
  // Where the pop-out hands an unconfirmed risk submission to the main window, which takes it over as its own.
  const submissionKey=()=>`openetrm.risk.handed-submission.${options.profile()}`;
  // The requests this window's storage keeps, which it now owns: a trade request it sent before a reload, or one the pop-out handed over
  // as it closed, and a risk submission the pop-out handed over, which is then the report's to reconcile.
  // A submission is taken from storage only once it is the report's: one refused stays there, said once, never lost.
  let refusedSubmission:string|undefined;
  function takeStoredRequest(){
    const before=!!pending.command||risk.unconfirmed();
    if(!pending.command)try{const saved=restoreTradeRequest(JSON.parse(sessionStorage.getItem(storageKey())??'null'));if(saved){pending.command=saved;uncertain=true;}}catch{/* An unreadable local draft grants no authority. */}
    if(options.window.kind==='main'){
      let handed:string|null=null;try{handed=sessionStorage.getItem(submissionKey());}catch{/* No storage, nothing handed over. */}
      if(handed!==null&&handed!==refusedSubmission){
        try{risk.adopt(JSON.parse(handed));sessionStorage.removeItem(submissionKey());}catch(error){refusedSubmission=handed;options.failed(error);}
      }
    }
    return !before&&(!!pending.command||risk.unconfirmed());
  }
  // The main window says in its storage that it keeps a risk submission of its own, which a pop-out's is then never handed over to replace.
  const keptSubmissionKey=()=>`openetrm.risk.kept-submission.${options.profile()}`;
  function markKeptSubmission(){
    if(options.window.kind!=='main'||!owner)return;
    try{if(risk.unconfirmed())sessionStorage.setItem(keptSubmissionKey(),'1');else sessionStorage.removeItem(keptSubmissionKey());}catch{/* Without storage no pop-out can be handed anything. */}
  }
  // Whether the pop-out's unconfirmed requests can go to the main window's storage, or why not: another request there is never overwritten,
  // and one that cannot be read is never taken for this one.
  // Each says what this window can then do with what it keeps (retainedActions).
  const retainedKinds=()=>({trade:!!pending.command,risk:risk.unconfirmed()});
  function handOverRefusal(storage:Storage):string|undefined{
    const keeps=()=>retainedActions(retainedKinds());
    try{
      const there=storage.getItem(requestKey(false));
      if(pending.command&&there!==null&&restoreTradeRequest(JSON.parse(there))?.operationId!==pending.command.operationId)
        return `The main window already holds another unconfirmed trade request, so this window keeps its own: ${keeps()}.`;
      if(risk.unconfirmed()&&(storage.getItem(submissionKey())!==null||storage.getItem(keptSubmissionKey())!==null))
        return `The main window already holds a risk submission handed to it, so this window keeps its own: ${keeps()}.`;
    }catch{return `The main window’s stored requests could not be read, so this window keeps its own: ${keeps()}.`;}
    return undefined;
  }
  // The pop-out hands its unconfirmed trade request and risk submission to the main window's storage, under the account's keys there, as the
  // Blotter goes back: they are then the main window's, with Check status, Retry and Reconcile, under their first identities. Undefined once
  // handed over; otherwise why they stay.
  function handOver(storage:Storage):string|undefined{
    const why=handOverRefusal(storage);if(why)return why;
    const submission=risk.handOff();
    try{
      if(pending.command)storage.setItem(requestKey(false),JSON.stringify(pending.command));
      if(submission)storage.setItem(submissionKey(),JSON.stringify(submission));
    }catch{return `The main window could not store the requests this window holds, so it keeps them: ${retainedActions(retainedKinds())}.`;}
    if(pending.command){pending.clear();uncertain=false;store();}
    if(submission)risk.handedOff();
    sync();return undefined;
  }
  // A request a pop-out handed over waits in this window's storage until its next idle look. A command of this window's own takes it over
  // first, so that storing the command never writes over it, and the command is then refused while it is outstanding, as any is (D238).
  function takeHanded(){if(takeStoredRequest())sync();}
  function store(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The current visit still retains the exact in-memory request. */}}
  function restore(){
    if(owner===options.profile()){takeStoredRequest();return;}
    owner=options.profile();pending.clear();uncertain=false;view=newBlotterView();showPanes();forgetViews();draftBase=undefined;field('blotter-name').value='';text('blotter-view-note','Named views are saved to your account. Dates and market inputs are selected separately.');get('blotter-column-choices').replaceChildren();text('blotter-table-title','Positions');workspace.closeMenus();viewControls();
    takeStoredRequest();
  }
  // The risk report is the panel's Risk report mode, and the strip says whether it is open, a queued run is under way, or a run's results
  // are on the desk, or at another context. The report tells the page whenever what it shows changes, also when a queued part finishes on
  // its own timer.
  function renderRisk(){
    if(!holdings)return;
    drawStrip();showRunBar();
  }
  // What the strip says of risk: a run on the desk as it stands, its results, refused or made elsewhere, or none at all, as no part has
  // any; before a run, whether its report is open or a queued run is under way, or what running one does, or why this account cannot.
  function stripRisk():StripRisk{
    const run=risk.current();
    if(inline?.state==='shown')return riskResults(inline);
    if(inline?.state==='failed')return {state:'unavailable',value:'Results not shown',reason:inline.reason};
    if(inline?.state==='elsewhere')return {state:'unavailable',value:'Results made elsewhere',reason:'The run on screen was made at another close, context or books than the Blotter read on screen. Export downloads its results.'};
    if(run&&!run.running&&!run.parts.some(p=>p.report))return {state:'unavailable',value:'No results',reason:gapsText(runGaps(run))};
    if(risk.running())return {state:'pending',value:'Running',reason:'A queued risk run is under way. Its results will show here and in the positions.'};
    if(panel.current()?.kind==='risk')return {state:'pending',value:'Set up in the panel',reason:'The risk report is open in the details.'};
    return {state:'none',reason:options.canRunRisk()?'Revalue the books on screen under shared market shocks.':riskDenied};
  }
  // The summary strip of the read on screen; the previous read's, stale, while the Blotter is read again or after that read failed; or,
  // while a first read's values are pending or failed, the positions read and why the totals are not shown.
  function drawStrip(){
    const host=get('blotter-strip');
    if(holdings&&summaries){strip=summaryStripItems(summaries,{count:holdings.holdings.length,unvalued:holdings.unvaluedPositions},stripRisk(),money);renderSummaryStrip(host,strip);return;}
    if(kept?.strip){renderSummaryStrip(host,staleStripItems(kept.strip,loading?.state==='failed'?failedStale[loading.read]:staleReason));return;}
    if(!facts){host.replaceChildren();return;}
    renderSummaryStrip(host,waitingStripItems(loading?.state==='failed'?{state:'unavailable',reason:loading.read==='count'?'Retry to read again':'Retry to value again'}:{state:'pending',reason:'Complete selected scope is being valued'},facts.total,money));
  }
  // What the run bar says of the run on screen, or nothing while there is none.
  function showRunBar(){
    const run=risk.current();
    if(!run||!holdings){renderRunBar(get('desk-run'),null,busy());return;}
    const results=inline===undefined||inline.key!==resultsKey(run)?{state:'none' as const}:inline.state==='shown'?{state:view.mode==='pivot'?'pivot' as const:'shown' as const,excluded:inline.excluded}
      :inline.state==='failed'?{state:'failed' as const,reason:inline.reason}:{state:'elsewhere' as const};
    renderRunBar(get('desk-run'),{...runBar(run,{zone:options.zone(),knowledge:knowledgeWords[options.knowledge()],results}),setup:run.setupText},busy());
  }
  // The results of a run are the reports its parts returned, and what became of the parts that returned none, at the read on screen. A
  // queued run has none to show while a part of it is under way: a part not yet done would read as moving nothing.
  function resultsKey(run:RunView|undefined){
    if(!run||run.running||!holdings||!run.parts.some(p=>p.report))return '';
    const id=(report:RiskReport)=>{let number=reportIds.get(report);if(number===undefined)reportIds.set(report,number=++reports);return number;};
    return JSON.stringify([run.key,readPlace?.snapshotId??null,holdings.context.contextId,
      run.parts.map(p=>p.report?id(p.report):p.failure!==undefined?'failed':p.unknown?'unknown':p.unlisted?'unlisted':'none')]);
  }
  /**
   * A run's results as the desk shows them: only against the read of the close, context and books they were run at, and only once every
   * part says which positions it leaves out, the same ones, and the positions it values and leaves out are those of the read on screen.
   * Their display text is the backend's, in each position's own deal format, prepared before they are shown.
   */
  async function inlineResults(run:RunView,key:string):Promise<Inline>{
    const read=holdings!,parts=run.parts.flatMap(p=>p.report?[p.report]:[]);
    if(!readPlace||!madeHere(run,readPlace))return {key,state:'elsewhere'};
    try{
      const excluded=runExclusions(parts),cells=lensCells(parts),held=new Set(read.holdings.map(positionKey)),left=new Set(excluded.map(positionKey));
      if(read.holdings.some(h=>cells.has(positionKey(h))===(h.unavailableReason!==null)||left.has(positionKey(h))!==(h.unavailableReason!==null))
        ||[...cells.keys(),...left].some(k=>!held.has(k)))
        throw Error('The results of this run do not match the positions of the Blotter on screen. Run it again.');
      await prepareAmountDisplay({rows:read.holdings.flatMap(h=>(cells.get(positionKey(h))?.shocks??[]).map(shock=>({amountFormat:h.amountFormat,
        upPnl:shock.up,downPnl:shock.down,basePv:shock.base,upPv:shock.upValue,downPv:shock.downValue,delta:shock.delta,gamma:shock.gamma}))),
        // The delta ladder's totals by book and measure (D230), in the desk's own precision: a total has no position's format.
        totals:parts.flatMap(r=>r.factors.flatMap(f=>f.totals.map(t=>({upPnl:t.values.upPnl,downPnl:t.values.downPnl,delta:t.values.delta}))))},body=>api<string[]>('amounts/display',body));
      return {key,state:'shown',run:run.key,cells,excluded,summary:runSummary(parts,excluded),scales:changeScales(cells),gaps:runGaps(run)};
    }catch(error){
      if((error as {code?:string}).code==='access_denied')throw error;
      return {key,state:'failed',reason:error instanceof Error&&error.message?error.message:'The results of this run could not be shown.'};
    }
  }
  // The report's run changed: a run with new results has them prepared and shown, and the first results of a run turn the lens to Risk.
  function riskChanged(){
    const run=risk.current(),key=resultsKey(run);
    if(key===''){if(inline!==undefined||preparing!==undefined){inline=undefined;preparing=undefined;showInline();}else renderRisk();return;}
    if(key===inline?.key||key===preparing){renderRisk();return;}
    preparing=key;renderRisk();
    inlineResults(run!,key).then(next=>{
      if(preparing!==key)return;
      preparing=undefined;inline=next;
      if(next.state==='shown'&&lensFor!==next.run){lensFor=next.run;lens='risk';}
      showInline();
    },()=>{if(preparing===key){preparing=undefined;clear();}});
  }
  // Draws the desk again for the results it shows inline: the lens, the change chart, the tile, the run bar and the panel's Risk tab.
  function showInline(){
    if(inline?.state!=='shown')lens='values';
    if(!catalog||!holdings||!summaries){renderRisk();return;}
    try{captureFilters();render();}
    catch(error){inline={key:inline?.key??'',state:'failed',reason:error instanceof Error&&error.message?error.message:'The results of this run could not be shown.'};lens='values';renderRisk();}
    options.reported();
  }
  // The Risk lens shows a run's results in the trade rows; a pivot view has no lens.
  const lensShown=()=>lens==='risk'&&inline?.state==='shown'&&view.mode==='trades';
  // The Ladder lens shows a run's per-factor totals in place of the trade rows (D230).
  const ladderOn=()=>lens==='ladder'&&inline?.state==='shown'&&view.mode==='trades';
  // What the strip states of a run's results: what it ran and its shock count, its largest move in each currency, the largest known one
  // while a part has no results, which position it is and why; or that nothing moves; with what the run leaves out, which positions are
  // not known and why.
  function riskResults(shown:Extract<Inline,{state:'shown'}>):StripRisk{
    const held=holdings!.holdings,at=new Map(held.map((h,i)=>[positionKey(h),i])),run=risk.current();
    const unknown=shown.gaps.length?[...shown.cells.values()].filter(cell=>!cell.shocks.length).length:0;
    const words=tileWords({largest:shown.summary.largest.length,gaps:shown.gaps,unknown,excluded:shown.excluded.length});
    return {state:'results',label:['Risk',run?.setup?tileReports[run.setup.report]:'retained run',...(run?[shocksText(shockCount(run))]:[])].join(' · '),note:words.note,empty:words.empty,
      moves:shown.summary.largest.map(largest=>{
        const h=held[at.get(largest.positionKey)!];
        return {currency:largest.currency,text:signedText(largest.change,holdingFormats(h).money),exact:largest.change,sign:signOf(largest.change),
          hint:`${words.lead}: ${largest.tradeReference} · ${positionRows([h],options.zone())[0].market}, if ${largest.shock.label} moves ${largest.direction}`};
      })};
  }
  // What the Risk lens shows of a position: the shocks that move it; that nothing moves it, or, while a part has no results, that it is
  // not known; one the run leaves out says so with the reason its own row states, which is why the run leaves it out (D220).
  function lensRow(shown:Extract<Inline,{state:'shown'}>,h:Holding,row:DeskRow):LensRowView{
    const cell=shown.cells.get(positionKey(h));
    if(!cell)return {state:'left',reason:row.unavailable??'Not valued at this close'};
    const moves=positionMoves(cell,shown.gaps);
    return moves.state==='shocks'?{state:'shocks',shocks:moves.shocks,scale:shown.scales.get(moves.shocks[0].currencyKey)}:moves;
  }
  // What the run on screen says of the position at a row, for its panel's Risk tab.
  function positionRisk(h:Holding,row:DeskRow):PositionRisk{
    if(inline?.state==='elsewhere')return {state:'elsewhere'};
    if(inline?.state==='failed')return {state:'failed',reason:inline.reason};
    const run=risk.current();
    if(inline?.state!=='shown')return risk.running()?{state:'running'}:run&&!run.parts.some(p=>p.report)?{state:'empty',reason:gapsText(runGaps(run))}:{state:'none'};
    const cell=inline.cells.get(positionKey(h));
    if(!cell)return {state:'left',reason:row.unavailable??'Not valued at this close'};
    return {state:'shown',cell,factors:inline.summary.factors,gaps:inline.gaps};
  }
  // Risk report opens the panel in its Risk report mode, asking first about edits the panel holds, before the page is busy, and reads the
  // risk catalog of the read on screen unless the report already holds one.
  const riskMode:PanelMode={kind:'risk'};
  async function openRiskReport(){
    if(busy()||!options.canRunRisk()||!holdings)return;
    if(!await panel.show(riskMode))return;
    panel.reveal();
    await act(risk.open);
  }
  // Risk report is offered to an account that can run scenarios once the Blotter is read, and says why to one that cannot.
  function syncRiskReport(){
    const button=get<HTMLButtonElement>('blotter-risk');
    button.hidden=false;button.disabled=busy()||!options.canRunRisk()||!holdings;button.title=options.canRunRisk()?'Revalue the selected books under shared market shocks':riskDenied;
  }
  // New trade is offered where the backend allows booking, and says why where it does not, in words under the toolbar that the button points
  // assistive technology at: not in a tooltip, and not left for the person to find out by pressing it.
  function syncNewTrade(){
    const control=bookingControl(),button=get<HTMLButtonElement>('futures-new');
    get('new-trade-availability').hidden=control.reason===null;text('new-trade-availability',control.reason??'');
    button.disabled=busy()||!control.enabled;
    if(control.reason===null)button.removeAttribute('aria-describedby');else button.setAttribute('aria-describedby','new-trade-availability');
  }
  function sync(){
    markKeptSubmission();
    // Release the rows before restoring their control's focus. On entry to
    // busy, position.sync captures that focus before the rows become inert.
    if(!busy())workspace.busy(false);
    const awaitingValues=!holdings&&loading!==undefined;get('blotter-workspace').querySelectorAll<HTMLElement>('.table-menu').forEach(menu=>{menu.inert=busy()||awaitingValues&&menu.id!=='blotter-scope';});field('blotter-saved').disabled=busy()||awaitingValues;get<HTMLButtonElement>('blotter-retry').disabled=busy();
    // Rows on screen while no valued read is, kept stale from the previous one, are not offered: opening them only says why.
    crudeFacts.querySelectorAll<HTMLButtonElement>('button').forEach(button=>button.disabled=busy());const grid=get('desk-positions').querySelector('[role=grid]');if(holdings)grid?.removeAttribute('aria-disabled');else grid?.setAttribute('aria-disabled','true');syncLens();booking.sync();risk.sync();editor.sync();swapEditor.sync();crudeOperations.sync();options.tickets.sync();syncProductPending();position.sync();entry.sync();panel.place();syncRiskReport();if(busy())workspace.busy(true);syncNewTrade();retained.hidden=!pending.command;if(pending.command)text('futures-pending-text',retainedText(pending.command));get<HTMLButtonElement>('blotter-export').disabled=busy()||!holdings||!table?.ready();get<HTMLButtonElement>('futures-retry').disabled=busy()||!pending.command||!retryAllowed(pending.command.path,catalog);get<HTMLButtonElement>('futures-reconcile').disabled=busy();if(view.mode==='pivot')get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(['instrumentType','unit','currency','priceUnit'].includes(n.value))n.disabled=true;});}
  function clearDesk(){get('desk').hidden=true;get('desk-charts').hidden=true;get('blotter-strip').replaceChildren();clearDeskCharts(get('desk-chart-pv'));clearDeskCharts(get('desk-chart-position'));for(const id of ['desk-chart-books','desk-chart-mix'])get(id).replaceChildren(get(id).querySelector('h3')!);}
  function cancelRead(){generation++;pricingController?.abort();pricingController=undefined;}
  function clear(){clearRead(false);}
  // A read again keeps on screen what the read before drew, its rows and the summary strip, charts and summary line, all marked stale
  // with the reason until the new values land.
  const staleReason='Reading the Blotter again · these values are from the previous read.',failedStale={
    valuation:'The Blotter could not be valued again · these values are from the previous read.',count:'The positions could not be counted again · these values are from the previous read.'};
  // Each pane that keeps values on screen says why itself, so the reason shows whichever panes are open.
  const staleNote=(id:string,host:HTMLElement)=>{const note=document.createElement('p');note.id=id;note.className='table-stale-note';note.setAttribute('role','status');note.hidden=true;host.prepend(note);return note;};
  const staleNotes=[staleNote('desk-stale-note',get('desk')),staleNote('charts-stale-note',get('blotter-pane-charts')),staleNote('details-stale-note',get('blotter-pane-details'))];
  // The grid's own mark is cleared by the grid once its new rows are in (`rows:false`); a pivot's chart's once it is read (`chart:false`).
  function markStale(reason:string|null,rows=true,chart=true){
    if(rows)workspace.stale(reason);
    for(const id of ['desk','desk-charts',...(chart?['blotter-chart-card']:[]),'futures-summary','position-details'])get(id).classList.toggle('stale',reason!==null);
    for(const note of staleNotes)if(chart||note.id!=='charts-stale-note'){note.textContent=reason??'';note.hidden=reason===null;}
  }
  // Forgets the read on screen. A new read for the same account keeps what the page draws, its rows marked stale, so the read arriving
  // changes the grid in place and the person keeps their place in it; the panel closes as it does for any new read.
  function clearRead(keepGrid:boolean){
    kept=keepGrid?{strip:strip??kept?.strip}:undefined;strip=undefined;
    // A read again keeps the position the details show, which the read arriving draws again (deskPanel.sync) or, when it no longer holds it,
    // closes; anything else the details hold closes now.
    const keepDetails=keepGrid&&panel.current()?.kind==='position';
    if(keepGrid){if(!keepDetails)table?.selectRow(null);markStale(staleReason);}else markStale(null);
    cancelRead();crudeFacts.replaceChildren();facts=undefined;factsRequest=undefined;arrived=[];showLoad(undefined);get('futures-page').dataset.pricingState='empty';delete get('futures-page').dataset.pricingContext;readInputs=undefined;readPlace=undefined;chartState=undefined;drawn=undefined;lens='values';ladderTable=undefined;ladderShown=undefined;inline=undefined;preparing=undefined;lensFor=undefined;booking.reset();editor.reset();swapEditor.reset();crudeOperations.reset();risk.reset();catalog=undefined;holdings=undefined;summaries=undefined;shapes=undefined;drafts.reset();table=undefined;if(!keepDetails)deskPanel.reset();lastReadRequest=undefined;text('header-calculation-time','');
    // The summary strip kept is drawn stale now: a read for the priced contracts reads no position count, which would draw it.
    if(keepGrid)drawStrip();
    if(!keepGrid){get('blotter-chart').querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(get('blotter-chart'));get('blotter-chart-card').hidden=true;get('blotter-books').replaceChildren();get('blotter-portfolios').replaceChildren();workspace.clear();clearDesk();text('futures-summary','Choose a market date to inspect holdings.');}
    sync();}
  // Starts the Blotter afresh: for another close or context, or for the Blotter opened again, each of which says so of a risk run it
  // clears; or, for another account, saying nothing, as everything the page held goes.
  function reset(cause:ReadCause|null){
    // Another account ends a Blotter popped out for the last one, which the account's change said first.
    if(cause===null&&tie?.out()){tie.end('account_changed');placeWindow();}
    if(cause)replaceRead(cause);else clear();restore();forgetViews();viewControls();snapshot='';get('futures-pending').hidden=true;}
  // A new read of the Blotter replaces the one a run on screen was made at: the run is cleared and the page says why (D222). A live
  // correction and a settlement record say so in their own words.
  function replaceRead(cause:ReadCause,keepGrid=false){if(risk.current())options.notice(readNotes[cause]);clearRead(keepGrid);}
  function choose(id:string,rows:{id:string;name:string}[],selected:string,placeholder='Choose…'){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option(placeholder,''),...rows.map(r=>new Option(publicText(r.name),r.id)));select.value=rows.some(r=>r.id===selected)?selected:'';}
  // A trade row's fields as the trade columns and exports state them: exact amounts, readable names.
  function tradeFields(h:Holding):Record<string,string|null>{
    const {booking,amountFormat,fixedAmountFormat,commodity,quantitySpecification,currencyReference,projectionCurves,coverage,...fields}=h;
    return {...fields,priceUnit:h.instrumentType==='ppa'?null:priceUnit(h),tradeDate:booking.tradeDate,legalEntity:booking.legalEntity??null,counterparty:booking.counterparty??null,instrumentType:friendly(h.instrumentType),commodity:commodity.name,market:h.marketLabel??curveName(h.curve),contract:deliveryLabel(h.deliveryFrom,h.deliveryTo,options.zone()),executed:displayInstant(h.executedAt,options.zone()),settlement:publicText(h.settlement==='unprocessed'?'Unprocessed':h.settlement)};
  }
  // What a trade row's value cell says when the close cannot value it, on screen and in exports (D220).
  function notValued(h:Holding,key:string){return h.unavailableReason&&valueFields(h.instrumentType).includes(key)?`Not valued · ${valuationReason(h.unavailableReason,h.instrumentType)}`:undefined;}
  // What a trade row's cell says where the backend states no single figure for a reason of the position's own: a scheduled swap's
  // quantities and prices by period, or a mark a lifecycle valuation does not state. Exports say it as the cells do.
  function noted(row:DeskRow|undefined,key:string){return (key==='quantity'?row?.quantityNote:key==='fixedPrice'?row?.priceNote:key==='mark'?row?.markNote:null)??undefined;}
  // Trade columns a desk view can add beside its own; the fields its own present are always on.
  const tradeColumns=blotterColumns.filter(([key])=>key!=='tradeCount'&&!isPriceMeasure(key));
  const money=(value:string)=>amountText(value,'money'),quantity=(value:string)=>amountText(value,'quantity');
  function render(){
    if(!catalog||!holdings||!summaries)return;
    const pivot=view.mode==='pivot',colors=commodityColors(holdings.holdings.map(h=>h.commodity.code));
    // Everything the desk shows is selected from the read before anything is drawn, so a read the desk refuses leaves the
    // desk on screen whole, and never a new strip and rows beside the old charts. The strip, the totals by book and the position mix
    // summarize the read in both display modes; the two market charts and grouped positions are the trade-row desk.
    const items=summaryStripItems(summaries,{count:holdings.holdings.length,unvalued:holdings.unvaluedPositions},stripRisk(),money);
    const tiles=summaryTiles(summaries),mix=positionsTile(summaries,holdings.holdings.length,holdings.unvaluedPositions);
    const plan=pivot?undefined:planDesk(colors);
    // A new future's form in the panel is filled again from the read, keeping what the person chose and typed that it still offers.
    if(booking.mounted())booking.populate();
    // The grid is built once for an account and a kind of rows; new reads, grouping, columns and filters change it in place.
    const scope=options.profile();
    get('desk').hidden=false;get('desk-charts').hidden=false;get('desk-chart-pv').hidden=get('desk-chart-position').hidden=pivot;
    strip=items;renderSummaryStrip(get('blotter-strip'),items);renderBookBars(get('desk-chart-books'),tiles,money);renderPositionMix(get('desk-chart-mix'),mix,colors);
    if(plan)renderDesk(scope,colors,plan);else renderPivot(scope);
    text('blotter-table-title',pivot?view.splitBy.length?'Pivot table':'Pivot summary':ladderOn()?'Delta ladder · grouped by curve':view.rowGroup===null?'Positions':`Positions · grouped by ${rowGroupLabel(view.rowGroup).toLowerCase()}`);
    // The ladder groups its rows by curve, so the trade rows' grouping is not offered beside it.
    get('blotter-row-group-control').hidden=pivot||ladderOn();
    workspace.describe(pivot?[
      {label:'Rows',values:view.groupBy.filter(d=>!view.splitBy.includes(d)).map(d=>groupChoices.find(c=>c[0]===d)![1])},
      {label:'Columns',values:view.splitBy.map(d=>groupChoices.find(c=>c[0]===d)![1])},
      {label:'Values',values:view.measures.map(m=>measureChoices.find(c=>c[0]===m)![1])},
    ]:[]);
    text('futures-summary',`${holdings.holdings.length} trade bindings · ${holdings.evaluatedContracts} evaluated futures contract prices. Financial swaps are valued from their side schedules.`);
    // Drawn whole: what was kept on screen stale is current again, but for a pivot's chart, which is read after the rows and stays marked
    // until it is (renderChartPanels). A draw that fails above leaves it marked.
    const chartComing=pivot&&view.chart.visible&&get('blotter-chart-card').classList.contains('stale');
    kept=undefined;markStale(null,false,!chartComing);
    options.onReadContext(holdings.asOf,holdings.context.knownAt);sync();syncPositionDetails();drawContract();
  }
  // What the trade rows' desk shows, selected from the read: its rows, their trade fields, its group headers and its two charts.
  function planDesk(colors:ReadonlyMap<string,string>){
    const held=holdings!.holdings;
    return {rows:positionRows(held,options.zone()),fields:held.map(tradeFields),groups:deskGroups(view.rowGroup,summaries!,held),
      pvPanels:pvChartPanels(summaries!,colors,money),positionPanels:positionChartPanels(summaries!,held,colors,quantity)};
  }
  function renderDesk(scope:string,colors:ReadonlyMap<string,string>,plan:ReturnType<typeof planDesk>){
    const held=holdings!.holdings,{rows,groups}=plan,shown=lensShown()?inline as Extract<Inline,{state:'shown'}>:undefined;
    const formats=held.map(holdingFormats);
    // The Ladder lens draws the run's per-factor totals in place of the trade rows (D230); the charts below are drawn either way.
    if(ladderOn())renderLadder(scope);
    else drawTrades(scope,held,colors,{rows,fields:plan.fields,groups,formats},shown);
    renderDeskCharts(get('desk-chart-pv'),plan.pvPanels,'No swaps in the selected books');
    // After a run, the long and short chart becomes the change if each contract moves up; it leaves out what the run leaves out (D220).
    const charted=shown??(ladderOn()?inline as Extract<Inline,{state:'shown'}>:undefined);
    text('desk-chart-position-title',charted?'Change if each contract moves up':'Long and short by market');
    if(charted){
      const change=changeChart(rows.map((row,i)=>({key:positionKey(held[i]),reference:row.reference,market:row.market,commodity:row.commodity})),charted.cells,colors,(i,value)=>formats[i].money(value));
      const excluded=valuationNote(charted.excluded.length);
      const unmoved=unmovedNote(change.unmoved,charted.gaps);
      renderDeskCharts(get('desk-chart-position'),change.panels,noMoveText(charted.gaps),[...(excluded?[excluded]:[]),...(unmoved?[unmoved]:[])]);
    }else renderDeskCharts(get('desk-chart-position'),plan.positionPanels,'No positions in the selected books');
    drawn={rows,colors};
  }
  // The trade rows of these positions, under the groups given or none, with the Risk lens's results when it shows them.
  function drawTrades(scope:string,held:Holding[],colors:ReadonlyMap<string,string>,{rows,fields,groups,formats}:{rows:DeskRow[];fields:Record<string,string|null>[];groups:ReturnType<typeof deskGroups>;formats:ReturnType<typeof holdingFormats>[]},shown?:Extract<Inline,{state:'shown'}>){
    ladderTable=undefined;ladderShown=undefined;
    // Every other trade column is present, shown when the view chooses it, so a filter on a hidden one stays active. Both lenses' columns
    // are present too, and switching the lens shows the other's in place (tradeHead).
    const details=tradeColumns.filter(([key])=>!(deskFields as readonly string[]).includes(key));
    const head=tradeHead,columns=[...head,...details];
    tableColumns=columns.map(c=>c[0]);transientKeys=new Set(['curve',...riskKeys]);
    const numeric=tableColumns.flatMap((key,i)=>i>=head.length?(numericFields.includes(key)?[i]:[]):['quantity','executionPrice','mark','presentValue','riskUp','riskDown','riskDelta'].includes(key)?[i]:[]);
    const lenses=shown?held.map((h,i)=>lensRow(shown,h,rows[i])):undefined;
    // A lens cell sorts and filters by its exact amount, which only a position one shock moves has; the others by their words.
    const single=(i:number)=>lenses?.[i].state==='shocks'&&lenses[i].shocks.length===1?lenses[i].shocks[0]:undefined;
    const lensWords=(i:number)=>{const lens=lenses![i];return lens.state==='left'?'Left out':lens.state==='unmoved'?'Not moved':lens.state==='unknown'?'Not known'
      :lens.shocks.length>1?`${lens.shocks.length} shocks`:`±${lens.shocks[0].bump} ${lens.shocks[0].unit}`;};
    const values=rows.map((row,i)=>[row.reference,row.market,row.quantity??'',row.price??'',row.mark??'',row.value??'','',
      ...(shown?[lensWords(i),single(i)?.up??'',single(i)?.down??'',single(i)?.delta??'']:['','','','']),...details.map(([key])=>fields[i][key]??'')]);
    const headers=new Map(groups?.headers.map(h=>[h.key,h])??[]);
    const cells=deskCells(rows,colors,{money:(i,value)=>formats[i].money(value),quantity:(i,value)=>formats[i].quantity(value),price:(i,value)=>formats[i].price(value),mark:(i,value)=>formats[i].mark(value),
      factor:(i,value)=>formats[i].factor(value)},i=>shapeOf(held[i]),tableColumns,lenses&&(i=>lenses[i]));
    // How many of a book's, or an instrument type's, positions the run leaves out, by the book and instrument type the run names for each.
    const leftOut=(key:string)=>shown?shown.excluded.filter(e=>view.rowGroup==='book'?e.bookId===key:view.rowGroup==='instrumentType'?e.instrumentType===key:false).length:0;
    table=workspace.update('desk-positions',scope+':trades',columns.map(c=>c[1]),values,numeric,busy,{
      keys:tableColumns,rowKeys:held.map(rowKey),cells,searchText:rows.map((row,i)=>shown?`${row.words} ${lensWords(i)}`:row.words),visual:[curveColumn],rowHeight:46,stretch:true,
      visible:columns.map(([key],i)=>riskOnly.has(key)?!!shown:valuesOnly.has(key)?!shown:i<head.length||view.columns.includes(key)),
      // The Risk lens's columns follow the position's size; a saved view never keeps them (transientKeys).
      layout:{...view.grid,order:view.grid.order.flatMap(key=>key==='quantity'?[key,...riskOnly]:[key])},
      onLayout:layout=>{view.grid=withoutColumns(layout,transientKeys);},onReady:sync,
      group:groups?{keys:groups.keys,order:groups.headers.map(h=>h.key),header:(key,count)=>shown?riskGroupHeader(headers.get(key)!,count,leftOut(key)):groupHeader(headers.get(key)!,count,money)}:undefined,
      initial:{query:view.query,columns:tableColumns.map(key=>view.columnFilters[key]??null)},retainMissingFilters:true,
      // A row opened is brought into view once the details have taken their room, when they leave it beneath the grid's foot.
      onRowOpen:index=>perform({kind:'selectPosition',key:rowKey(held[index])}).then(shown=>shown===true),
      onRowSelect:index=>holdings&&following()?perform({kind:'selectPosition',key:rowKey(held[index])},{placed:true}).then(shown=>shown===true):true,
      onRowEscape:()=>void perform({kind:'details',open:false}),onRowMenu:(index,at)=>openRowMenu(held[index],at),onRowDoubleOpen:index=>popout(held[index])});
    workspace.columns(tradeColumns,[...new Set([...deskFields,...view.columns])],selected=>void perform({kind:'chooseColumns',columns:selected}),[...deskFields]);
  }
  // The reports of the run on screen whose parts have results: a part that failed or has none is said so by the run bar, never read as zero.
  const shownReports=()=>inline?.state==='shown'?risk.current()?.parts.flatMap(p=>p.report?[p.report]:[])??[]:[];
  // The Ladder lens: a row for each shocked factor, grouped by curve, and a column for each book, measure and currency, each cell the
  // server's own value (D230). Its rows are not positions, so the trade rows' filters, layout and selection are left as they are.
  function renderLadder(scope:string){
    const ladder=deltaLadder(shownReports(),ladderValue,curveName),exported=ladderExport(ladder,ladderValue),rows=ladder.groups.flatMap(g=>g.rows);
    const keys=['curve','contract','coordinate','shock','priceUnit','value',...ladder.columns.map((_,i)=>'book'+i)],first=keys.length-ladder.columns.length;
    // Each amount as the backend formats it in the desk's precision: delta as the lens shows it, P&L as money. The browser never rounds.
    const kind=ladderValue==='delta'?'factor' as const:'money' as const,shownText=(cell:LadderCell)=>cell.missing?'Not reported':amountText(cell.exact,kind);
    const display=exported.rows.map((row,i)=>row.map((value,c)=>c<first?value:shownText(rows[i].cells[c-first])));
    const cells=(index:number,column:number)=>{
      if(column<first)return null;
      const cell=rows[index].cells[column-first],span=document.createElement('span');
      if(cell.missing){span.className='ladder-missing';span.textContent='Not reported';return span;}
      span.textContent=publicText(shownText(cell));span.title=publicText(`${cell.exact} ${cell.unit} · up ${amountText(cell.upExact,'money')} · down ${amountText(cell.downExact,'money')}`);if(cell.zero)span.className='ladder-zero';
      if(cell.nonlinear){const mark=document.createElement('span');mark.className='ladder-nonlinear';mark.textContent='nonlinear';span.append(mark);}
      return span;
    };
    const groupKeys=ladder.groups.flatMap(g=>g.rows.map(()=>g.key)),titles=new Map(ladder.groups.map(g=>[g.key,g.title]));
    table=undefined;ladderShown=exported;
    ladderTable=workspace.update('desk-positions',scope+':ladder',exported.headers,exported.rows,exported.numeric,busy,{
      keys,rowKeys:rows.map(row=>row.factorId),displayRows:display,cells,searchText:display.map(row=>row.join(' ')),rowHeight:40,stretch:true,
      visible:keys.map(key=>!['curve','priceUnit','value'].includes(key)),layout:{order:keys,widths:{contract:150,coordinate:120,shock:190,...Object.fromEntries(ladder.columns.map((_,i)=>['book'+i,180]))},sort:[]},onLayout:()=>{},onReady:sync,
      group:{keys:groupKeys,order:ladder.groups.map(g=>g.key),header:(key,count)=>{const head=document.createElement('span');head.className='desk-group-head';
        head.textContent=publicText(`${titles.get(key)} · ${count} ${count===1?'shock':'shocks'}, each on its own`);return head;}},
      initial:{query:'',columns:keys.map(()=>null)},onRowOpen:index=>void openContract(rows[index].factorId)});
  }
  // A ladder row opens the panel's Contract mode: the positions its shock moves, as the server values them.
  async function openContract(factorId:string){
    if(busy())return;
    if(await panel.show({kind:'contract',factorId}))panel.reveal();
  }
  function drawContract(){
    const mode=panel.current();
    if(mode?.kind!=='contract')return;
    const reports=shownReports(),factor=reports.flatMap(r=>r.factors).find(f=>f.factor.id===mode.factorId);
    if(!factor){panel.reset();options.notice('The contract the panel showed is not in the run on screen, so the panel was closed.');return;}
    const ladder=deltaLadder(reports,ladderValue,curveName),group=ladder.groups.find(g=>g.rows.some(r=>r.factorId===mode.factorId))!,row=group.rows.find(r=>r.factorId===mode.factorId)!;
    const node=<K extends keyof HTMLElementTagNameMap>(tag:K,className:string,value:string)=>{const n=document.createElement(tag);n.className=className;n.textContent=publicText(value);return n;};
    const title=node('h2','side-panel-title',`${group.title} · ${row.period}`);title.id='side-panel-title';
    panel.element('head').replaceChildren(title,node('p','side-panel-sub',`${row.coordinate} · shocked ${row.shock}`));
    panel.element('tabs').replaceChildren();
    const body=panel.element('body');body.setAttribute('role','group');body.setAttribute('aria-label','Contract');
    const listed=contractPositions(factor,ladderValue),words={delta:'Delta',upPnl:'Up P&L',downPnl:'Down P&L'}[ladderValue];
    const list=document.createElement('ul');list.className='contract-positions';
    for(const position of listed.positions){
      const item=document.createElement('li'),button=document.createElement('button');button.type='button';button.className='secondary';
      const held=holdings?.holdings.find(h=>positionKey(h)===position.key),format=held?holdingFormats(held):undefined;
      button.append(node('span','',`${position.tradeReference} · ${position.label}`),node('span','',`${format?(ladderValue==='delta'?format.factor:format.money)(position.exact):position.exact} ${position.unit}`));
      button.title=publicText(`${position.exact} ${position.unit}`);
      button.onclick=()=>{const held=holdings?.holdings.find(h=>positionKey(h)===position.key);if(held)void perform({kind:'selectPosition',key:rowKey(held)});else options.notice(`${position.tradeReference} is not in this Blotter read.`);};
      item.append(button);list.append(item);
    }
    panel.element('view').replaceChildren(
      node('p','field-help',listed.positions.length?`${words} of each position this shock moves, as the server values it, largest first. Open one to see the position.`:'This shock moves no position in the books on screen.'),
      ...(listed.positions.length?[list]:[]),
      ...(listed.unmoved?[node('p','field-help',`${listed.unmoved} other held ${listed.unmoved===1?'position is':'positions are'} not moved by this shock.`)]:[]));
  }
  // The curve shape a position's market has at this read: its shape, why it has none, or nothing yet while the shapes are read.
  function shapeOf(h:Holding):CurveShape|string|undefined{
    if(!shapes)return undefined;
    if('unavailable' in shapes)return shapes.unavailable;
    if(h.definitionRevisionId!==shapes.revision)return otherRevision;
    if(projectsFromSeveralCurves(h))return severalCurvesShape;
    return shapes.curves.get(h.curve)??'Curve shape unavailable';
  }
  // The position at a row of the read on screen, as the details draw it; nothing while the desk draws no positions.
  function positionInput(index:number):PositionInput|undefined{
    const h=panelHoldings()?.[index],row=drawn?.rows[index];if(!h||!row)return;
    return {holding:h,row,formats:holdingFormats(h),shape:shapeOf(h),color:drawn!.colors.get(row.commodity)!,risk:positionRisk(h,row)};
  }
  function renderPivot(scope:string){
    const read=holdings!;drawn=undefined;
    // A pivot has no desk charts: free those the trade rows drew.
    clearDeskCharts(get('desk-chart-pv'));clearDeskCharts(get('desk-chart-position'));
    const pivotKeys=pivotColumns(view.groupBy,view.measures);
    const columns=blotterColumns.filter(([key])=>pivotKeys.includes(key));
    tableColumns=columns.map(c=>c[0]);
    // Every pivot subtotal says how many positions it leaves out whenever any group leaves one out (D220).
    const counted=read.groups.some(g=>excludedPositions(g.unvaluedPositions)>0);
    const rows:Record<string,string|null>[]=read.groups.map(g=>({...Object.fromEntries(columns.map(([key])=>[key,groupLabel(g,key,options.zone())])),unvaluedPositions:String(excludedPositions(g.unvaluedPositions))}));
    const required=['instrumentType','unit','currency','priceUnit'];
    const visible=columns.map(([key])=>view.columns.includes(key)||required.includes(key));
    if(!visible.some(Boolean))visible[0]=true;
    const numeric=tableColumns.flatMap((key,i)=>numericFields.includes(key)||isPriceMeasure(key)?[i]:[]);
    const matrix=view.splitBy.length?pivotMatrix(read.groups,view.groupBy,view.splitBy,counted?[...view.measures,'unvaluedPositions']:view.measures,options.zone()):undefined;
    const gridColumns:{key:string;title:string;numeric:boolean;measure?:string}[]=matrix?.columns??[...columns.map(([key,title],i)=>({key,title,numeric:numeric.includes(i)})),...(counted?[{key:'unvaluedPositions',title:'Not valued',numeric:true}]:[])];
    tableColumns=gridColumns.map(c=>c.key);
    transientKeys=new Set(gridColumns.filter(c=>(c.measure??c.key)==='unvaluedPositions').map(c=>c.key));
    const gridVisible=gridColumns.map(c=>transientKeys.has(c.key)||(c.key.startsWith('pivot:')?view.columns.includes(c.measure!):view.columns.includes(c.key)||required.includes(c.key)));
    const rawRows=matrix?.rows??rows;
    const displayRows=rawRows.map(row=>gridColumns.map(c=>{
      const value=row[c.key]??'';if(!c.numeric||value==='')return value;
      const measure=c.measure??c.key;
      if(['executionPrice','mark','fixedPrice'].includes(measure)||isPriceMeasure(measure))return amountText(value,'price');
      if(['presentValue','executionPnl'].includes(measure))return amountText(value,'money');
      if(measure==='quantity')return amountText(value,'quantity');
      return value;
    }));
    // A pivot row is named by its labels, the values of its columns that are not measures; rows its labels do not tell apart, by their order.
    const seen=new Map<string,number>();
    const rowKeys=rawRows.map(row=>{const label=gridColumns.filter(c=>!c.numeric).map(c=>row[c.key]??'').join('\u0000'),repeat=seen.get(label)??0;seen.set(label,repeat+1);return repeat?label+'\u0000'+repeat:label;});
    table=workspace.update('desk-positions',scope+':pivot',gridColumns.map(c=>c.title),rawRows.map(row=>gridColumns.map(c=>row[c.key]??'')),gridColumns.flatMap((c,i)=>c.numeric?[i]:[]),busy,{displayRows,keys:tableColumns,rowKeys,columnGroups:matrix?Object.fromEntries(matrix.columns.filter(c=>c.groupKey).map(c=>[c.key,{key:c.groupKey!,title:c.groupTitle!,measureTitle:c.measureTitle!}])):undefined,layout:view.grid,onLayout:layout=>{view.grid=withoutColumns(layout,transientKeys);},onReady:sync,visible:gridVisible,initial:{query:view.query,columns:tableColumns.map(key=>view.columnFilters[key]??null)},retainMissingFilters:true});
    // No column chosen is no choice: the choices are drawn again as they were.
    workspace.columns(columns,columns.filter((_,i)=>visible[i]).map(c=>c[0]),selected=>{if(selected.length)void perform({kind:'chooseColumns',columns:selected});else if(!busy())render();},required);
    get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(required.includes(n.value)){n.checked=true;n.disabled=true;}});
  }

  // A cleared source cannot receive the old result: where the person is then is nowhere, which no read was made for.
  function requireCurrentRead(started:number,scope:string){
    if(started!==generation||whereAmI()!==scope)throw Error('The account or market selection changed. Reload the Blotter before continuing.');
  }
  // Keep streamed rows private until the complete report and its context pass validation.
  function acceptValuedRows(batch:Holding[]){arrived=[...arrived,...batch];if(holdings)render();}
  // A read whose values or count failed keeps what the previous read drew, marked stale, or, on a first load, no rows; the grid pane says which
  // failed and why, with Retry.
  function showCrudeFacts(){
    crudeFacts.replaceChildren();if(!facts)return;
    const rows=facts.positions.filter(p=>p.instrumentType==='physical_crude');
    if(!rows.length&&facts.nextOffset===null)return;
    const title=document.createElement('h3');title.textContent='Physical crude · booked facts';crudeFacts.append(title);
    for(const row of rows){const button=document.createElement('button');button.type='button';button.dataset.tradeId=row.tradeId;button.textContent=`${row.tradeReference} · delivery and cash`;button.onclick=()=>void operateCrude(row.tradeId);crudeFacts.append(button);}
    if(facts.nextOffset!==null){
      const more=document.createElement('button');more.type='button';more.id='crude-facts-more';more.textContent=`Read more booked facts (${facts.positions.length} of ${facts.total} read)`;
      more.onclick=()=>{if(!busy())void act(async()=>{
        const before=facts,query=factsRequest,started=generation,place=whereAmI();
        if(!before||before.nextOffset===null||!query)return;
        try{
          const page=await api<unknown>('trading/positions',{...query,offset:before.nextOffset,limit:factPageSize});
          if(started!==generation||place!==whereAmI()||facts!==before)return;
          facts=appendPositionFacts(before,page,factPageSize);showCrudeFacts();sync();
        }catch(error){if(started===generation&&place===whereAmI()&&(error as {code?:string}).code==='access_denied')clear();throw error;}
      });};
      crudeFacts.append(more);
    }
    crudeFacts.querySelectorAll<HTMLButtonElement>('button').forEach(button=>button.disabled=busy());
  }
  function readFailed(error:unknown,read:'count'|'valuation'){
    get('futures-page').dataset.pricingState='failed';
    showLoad({state:'failed',read,reason:error instanceof Error&&error.message?error.message:read==='count'?'The positions could not be counted.':'The positions could not be valued.'});
    if(kept)markStale(failedStale[read]);
    drawStrip();showCrudeFacts();sync();
  }
  async function load(bookingCatalog:BookingCatalog='directory',live=false,background=false){
    // A Blotter in a window of its own is read there; one that keeps only its retained request reads nothing.
    if(tie?.out()||retainedOnly)return;
    // A read awaiting its values has only its book scope to change; its other controls belong to the view.
    crudeFacts.replaceChildren();if(!live)factsRequest=undefined;
    const account=owner;restore();if(!viewsLoaded)await loadViews();view=parseBlotterViews(JSON.stringify([!holdings&&loading?{...view,bookIds:selected('blotter-books'),portfolioIds:selected('blotter-portfolios')}:readView()]))[0];const source=options.source();
    if(source.context&&!source.context.contextId)throw Error('Select a retained historical context before reading holdings.');
    snapshot=source.snapshotId;
    const inputs=JSON.stringify([source.snapshotId,source.context?.contextId??null,view.bookIds,view.portfolioIds]);
    // A read for the account on screen keeps its grid, stale, and the read arriving changes it in place: also the rows a failed read kept.
    const keep=!live&&(!!holdings||!!kept)&&account===owner;
    if(live)cancelRead();else replaceRead(inputs===readInputs?'read':'inputs',keep);
    const started=generation,scope=JSON.stringify([options.profile(),source]);
    const request:ReadRequest={snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})};
    const selectedView={mode:view.mode,bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds],groupBy:view.mode==='pivot'?[...view.groupBy]:[],measures:view.mode==='pivot'?[...view.measures]:[],pivotConvention:view.mode==='pivot'?'trading_blotter_totals_v1':null,priceNumericalPolicy:view.mode==='pivot'&&view.measures.some(m=>isPriceMeasure(m)&&m.endsWith('Mean'))?priceMeanPolicy:null};
    const summaryViews=deskSummaryViews(view.bookIds,view.portfolioIds);
    get('futures-page').dataset.pricingState='pending';if(!live)showLoad({state:'reading'});
    let factsRead:Promise<void>=Promise.resolve(),countFailed:unknown;
    try{
      if(!live&&bookingCatalog==='directory'){
        const directory=await api<Catalog>('trading/directory',{...request,convention:'trading_workspace_v1'});
        requireCurrentRead(started,scope);if(get('futures-page').hidden)return;
        if(directory.kind!=='directory'||Object.hasOwn(directory,'contracts')||!directory.context?.contextId||!Array.isArray(directory.books)||
          source.context&&directory.context.contextId!==source.context.contextId)throw Error('The trading directory returned an inconsistent context. Refresh the Blotter.');
        catalog=directory;viewControls();
        // The count of booked positions is read beside the valuation, which waits for it only to check the bindings it values.
        factsRead=(async()=>{
          const query={context:{...request,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:directory.context.contextId},bookIds:selectedView.bookIds,portfolioIds:selectedView.portfolioIds};
          const page=await api<unknown>('trading/positions',{...query,offset:0,limit:factPageSize});
          requireCurrentRead(started,scope);
          const read=readPositionFacts(page,factPageSize);
          if(read.context.contextId!==directory.context.contextId||read.context.effectiveOn!==directory.context.effectiveOn||read.context.knownAt!==directory.context.knownAt||read.asOf!==directory.asOf)throw Error('The position facts returned a different directory context. Refresh the Blotter.');
          facts=read;factsRequest=query;if(get('futures-page').hidden||holdings)return;
          if(loading?.state==='reading')showLoad({state:'valuing',total:read.total});
          get('desk').hidden=false;drawStrip();if(!keep)options.onReadContext(read.asOf,read.context.knownAt);sync();
        })().catch(error=>{countFailed=error;throw error;});
        // The valuation awaits it, and fails with it; the failure is then said as the count's own.
        factsRead.catch(()=>undefined);
      }
    }catch(error){if(started===generation)clear();throw error;}
    const controller=background&&!live&&bookingCatalog==='directory'?new AbortController():undefined;
    pricingController=controller;
    const finish=async()=>{
      try{
        // A working valuation remains a current read so its booking permissions
        // remain current. Historical valuations retain the selected context.
        type Combined={catalog:Catalog;blotter:Holdings;summaries:Summary[]};
        const body={context:{...request,convention:'trading_workspace_v1'},view:selectedView,bookingCatalog,summaries:summaryViews};
        let combined:Combined;
        if(controller&&!keep&&selectedView.mode==='trades'){
          let first:BlotterStreamStart|undefined,complete:Combined|undefined;
          const seen=new Set<string>();
          await options.stream(body,{
            start:async value=>{
              await factsRead;requireCurrentRead(started,scope);
              if(!value.context||['contextId','effectiveOn','knownAt'].some(k=>typeof value.context[k as keyof typeof value.context]!=='string'||!value.context[k as keyof typeof value.context])||
                !facts||value.positions!==facts.total||value.asOf!==facts.asOf||source.context&&['contextId','effectiveOn','knownAt'].some(k=>value.context[k as keyof typeof value.context]!==facts!.context[k as 'contextId'|'effectiveOn'|'knownAt']))throw Error('The streamed valuation returned a different position context. Refresh the Blotter.');
              first=value;
            },
            rows:async(batch,valued,total)=>{
              requireCurrentRead(started,scope);
              for(const row of batch){
                if(typeof row.tradeId!=='string'||typeof row.bindingId!=='string')throw Error('The streamed valuation returned an invalid position.');
                const key=JSON.stringify([row.tradeId,row.bindingId]);if(seen.has(key))throw Error('The streamed valuation repeated a position.');seen.add(key);
              }
              acceptValuedRows(batch as Holding[]);showLoad({state:'valuing',total,completed:valued});
            },
            complete:value=>{
              requireCurrentRead(started,scope);
              const result=value as unknown as Combined,header=first;
              if(!header||[result.catalog,result.blotter].some(part=>part.asOf!==header.asOf||['contextId','effectiveOn','knownAt'].some(k=>part.context?.[k as keyof Context]!==header.context[k as keyof typeof header.context])))throw Error('The streamed valuation completed under a different context. Refresh the Blotter.');
              complete={...result,blotter:{...result.blotter,holdings:[...arrived]}};
            }
          },controller.signal);
          requireCurrentRead(started,scope);controller.signal.throwIfAborted();
          if(!complete)throw Error('The streamed valuation did not complete. Refresh the Blotter.');
          combined=complete;
          await prepareAmountDisplay({blotter:combined.blotter,summaries:combined.summaries},values=>api<string[]>('amounts/display',values,controller.signal));
        }else combined=await api<Combined>('trading/workspace',{...body,...(controller?{progressive:true}:{})},controller?.signal);
        requireCurrentRead(started,scope);if(get('futures-page').hidden)return;
        const selected=combined.catalog.context,reported=combined.blotter.context;
        if(combined.catalog.kind!==bookingCatalog||combined.catalog.kind==='directory'&&Object.hasOwn(combined.catalog,'contracts')||combined.catalog.kind==='priced_contracts'&&!Array.isArray(combined.catalog.contracts))throw Error('The trading workspace returned an inconsistent booking catalog. Refresh and try again.');
        if(!selected.contextId||selected.contextId!==reported.contextId||selected.effectiveOn!==reported.effectiveOn||selected.knownAt!==reported.knownAt||combined.catalog.asOf!==combined.blotter.asOf)throw Error('The trading workspace returned inconsistent market contexts. Refresh and try again.');
        if(!Array.isArray(combined.summaries)||combined.summaries.length!==summaryViews.length)throw Error('The trading workspace did not return the desk summaries. Refresh and try again.');
        await factsRead;requireCurrentRead(started,scope);if(get('futures-page').hidden)return;
        if(facts)requirePricedFacts(facts,combined.blotter,!!source.context);
        const previous={catalog,holdings,summaries,shapes,lastReadRequest,readPlace};
        catalog=combined.catalog;holdings=combined.blotter;summaries=combined.summaries;shapes=undefined;drafts.reset();
        const reportRequest={context:{...request,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:selected.contextId},view:selectedView};lastReadRequest=reportRequest;
        readPlace={contextId:selected.contextId,asOf:combined.blotter.asOf,snapshotId:request.snapshotId,bookIds:[...selectedView.bookIds],portfolioIds:[...selectedView.portfolioIds]};
        try{viewControls();acceptValuedRows(holdings.holdings.slice(arrived.length));}catch(error){({catalog,holdings,summaries,shapes,lastReadRequest,readPlace}=previous);throw error;}
        facts=undefined;factsRequest=undefined;readInputs=inputs;showLoad(undefined);
        shapesRead=view.mode==='trades'?loadShapes(started,scope,source):Promise.resolve();
        await loadChart(reportRequest,started,scope);requireCurrentRead(started,scope);if(get('futures-page').hidden)return;
        get('futures-page').dataset.pricingContext=selected.contextId;get('futures-page').dataset.pricingState='complete';workspace.closeMenus();sync();
      }catch(error){
        if(started!==generation||get('futures-page').hidden)return;
        // A detached background read is discarded. An awaited booking read
        // must still report a changed selection to the action waiting for it.
        if(whereAmI()!==scope){if(controller)return;clear();throw error;}
        if((error as {code?:string}).code==='access_denied'){
          clear();
          // Clearing revokes this generation; report its refusal before the detached handler discards it.
          if(background&&!live&&bookingCatalog==='directory')options.failed(error);
        }
        // A Blotter read whose values were not drawn says why, once the count read beside it is in, and keeps what the previous read drew.
        else if(!live&&bookingCatalog==='directory'&&!holdings){if(arrived.length&&!kept){arrived=[];table=undefined;workspace.clear();}await factsRead.catch(()=>undefined);if(started!==generation||get('futures-page').hidden)return;readFailed(error,error===countFailed?'count':'valuation');}
        else if(!live)clear();
        else{get('futures-page').dataset.pricingState='failed';showCrudeFacts();if(shapes===undefined)noShapes('the refresh that interrupted their read failed');}
        // A read in the background is said by its own handler below; it still fails, for whoever awaits the values (valued).
        throw error;
      }finally{if(pricingController===controller)pricingController=undefined;}
    };
    pricingRead=finish();
    if(background&&!live&&bookingCatalog==='directory'){
      void pricingRead.catch(error=>{if(started===generation&&whereAmI()===scope&&!get('futures-page').hidden)options.failed(error);});
    }else await pricingRead;
  }
  // Every row's curve unavailable, with the reason.
  function noShapes(why:string){shapes={unavailable:`Curve shape unavailable · ${why}`};redrawCurves();syncPositionDetails();}
  // The rows' curve cells, once their shapes are read; the Risk lens draws none.
  function redrawCurves(){const column=tableColumns.indexOf('curve');if(column>=0)table?.redrawCells(column);}
  // Sparklines follow the first render: the held curves of the close's definitions, at most 64 to a read, in the market
  // context the positions were read in. A failed read leaves each of its curves unavailable with the reason, and never
  // blanks the positions.
  async function loadShapes(started:number,scope:string,source:{snapshotId:string;context?:{contextId?:string}}){
    const current=()=>{try{requireCurrentRead(started,scope);return true;}catch{return false;}};
    let revision:string;
    try{revision=options.definitionRevision();}catch{noShapes('the market definitions of this close are not loaded');return;}
    const curves=[...new Set(holdings!.holdings.filter(h=>h.definitionRevisionId===revision).map(h=>h.curve))].sort();
    const found=new Map<string,CurveShape|string>();
    for(let i=0;i<curves.length;i+=curvesPerShapeRead){
      for(const [curve,shape] of await readCurveShapes(api,source,curves.slice(i,i+curvesPerShapeRead)))found.set(curve,shape);
      if(!current())return;
    }
    shapes={revision,curves:found};redrawCurves();syncPositionDetails();
  }
  async function loadChart(reportRequest:{context:unknown;view:unknown},started:number,scope:string){
    // Trade rows have the desk's charts; a pivot keeps its configurable chart.
    if(!view.chart.visible||view.mode!=='pivot')return;
    const dimensions=view.groupBy,splitBy=view.splitBy;
    const chartRequest={context:reportRequest.context,view:{mode:'pivot',bookIds:view.bookIds,portfolioIds:view.portfolioIds,groupBy:dimensions,measures:[view.chart.measure],pivotConvention:'trading_blotter_totals_v1',priceNumericalPolicy:isPriceMeasure(view.chart.measure)&&view.chart.measure.endsWith('Mean')?priceMeanPolicy:null}};
    const report=await api<Holdings>('trading/blotter',chartRequest);requireCurrentRead(started,scope);
    // A chart of a valuation measure leaves unvalued positions out and says so; booked quantities count every position.
    const excluded=valuationMeasure(view.chart.measure)?valuationNote(report.unvaluedPositions):null;
    text('blotter-chart-scope','Follows the pivot rows and columns · selected books and portfolios, before table row filters.'+(excluded?' '+excluded:''));
    chartState={panels:pivotChartPanels(report.groups,dimensions,splitBy,view.chart.measure,options.zone()),request:chartRequest};renderChartPanels();
  }
  function renderChartPanels(){
    if(!chartState)return;const {panels,request}=chartState,host=get('blotter-chart');
    host.querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(host);host.classList.remove('chart-empty');get('blotter-chart-card').hidden=false;
    // The chart read, it is current again, and so is its pane once the desk's charts are.
    get('blotter-chart-card').classList.remove('stale');if(!get('desk-charts').classList.contains('stale')){const note=get('charts-stale-note');note.hidden=true;note.textContent='';}
    get('blotter-chart-card').querySelectorAll<HTMLButtonElement>('[data-plot-kind]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.plotKind===view.chart.kind)));
    if(!panels.length){host.classList.add('chart-empty');text('blotter-chart','No values available for the selected books and portfolios.');}
    for(const spec of panels){
      const panel=document.createElement('div');panel.className='chart';host.append(panel);
      renderChart(panel,{...spec,kind:view.chart.kind,exportImage:download=>void act(async()=>{try{await api('trading/blotter',request);}catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}download();})});
    }
  }
  get('blotter-chart-card').querySelectorAll<HTMLButtonElement>('[data-plot-kind]').forEach(button=>button.onclick=()=>{if(busy())return;view.chart.kind=button.dataset.plotKind as BlotterView['chart']['kind'];field('blotter-chart-kind').value=view.chart.kind;renderChartPanels();});
  // The form a request came from is done with it once what it held was sent, or is kept by the retained request: it is never offered again, so that
  // the same request is not offered twice. A new trade has nothing but its form, so the panel it was in is closed with it. A request that was refused
  // for good is kept by nothing, and what the person entered is not dropped unasked, a booking's or an amendment's: its form stays to be corrected,
  // and what was prepared for it is dropped, so that it is prepared again. A settlement command is neither: the settlement view it was sent from
  // keeps what it shows and says what became of it.
  function done(command:RequestCommand){
    if(isLifecycleCommand(command.path))return;
    const swap=command.path.startsWith('swaps/'),amendment=command.path.endsWith('/amend');
    if(!pending.command){if(swap)swapEditor.unprepare();else if(amendment)editor.unprepare();return;}
    if(command.path==='futures/amend')editor.reset();
    if(swap)swapEditor.reset();
    if(!amendment)closeNew();
  }
  // What the backend says of a trade request it has committed: the trade, and the binding the request left it with.
  type Receipt={tradeId?:string;bindingId?:string};
  async function send(command:RequestCommand){
    const retrying=uncertain;uncertain=true;store();
    let receipt:Receipt|undefined;
    try{receipt=await api<Receipt>(command.path,{...command.body,operationId:command.operationId});}
    catch(error){
      // A refusal for good releases the request with the backend's reason: on a retry too, where the backend answers a retry with the
      // original's outcome before it refuses one (releasesRetained).
      if(releasesRetained(command.path,(error as {code?:string}).code,retrying)){pending.clear();uncertain=false;store();}
      done(command);
      if((error as {code?:string}).code==='access_denied')clear();sync();throw error;
    }
    const amended=amendedPosition(command);
    // What was sent is no longer an edit: the read that follows closes the form without asking.
    done(command);
    pending.clear();uncertain=false;store();
    if(isLifecycleCommand(command.path)){await settled(command);return;}
    await load();
    await follow(amended,receipt);await followBooked(command,receipt);
  }
  // A settlement command that went through changes what its swap is valued at: risk results run before it no longer hold, and are
  // cleared, with a note when there were any, and an open risk report is read again at the new inputs, keeping a setup's choices (D221).
  // The Blotter is read again as a live correction reads it, so that the panel, and the settlement view the command was sent from, stay,
  // and that view clears what the command consumed and reads its state again: also when the Blotter cannot be read, as a form still
  // holding a command that went through would offer it a second time.
  async function settled(command:RequestCommand){
    const plan=riskRefreshPlan({open:panel.current()?.kind==='risk',edited:risk.edited(),run:!!risk.current(),retained:risk.unconfirmed()},'settlement');
    inputsChanged(plan);
    if(isCrudeOperation(command.path)){
      const place=whereAmI();
      const failure=await refreshAcceptedCrudeOperation(()=>load('directory',true),async()=>{if(place===whereAmI())await crudeOperations.reload();});
      if(failure&&place===whereAmI())options.notice('The operation was recorded. Contractual valuation is unavailable; delivery and cash are read separately.');
    }else{try{await load('directory',true);}finally{await swapEditor.reloadSettlement(command.operationId);}}
    if(plan.reopen)await risk.open();
  }
  // What the risk report holds belongs to inputs that have changed. It is cleared before the read, with the note, so that a read that then
  // fails leaves the note, as it leaves the desk it had; a setup's choices are carried over to the report's next read.
  function inputsChanged(plan:RiskRefresh){risk.inputsChanged(plan.carry);if(plan.note)options.notice(plan.note);}
  // The position the panel shows of the trade an amendment was made to, if it shows it.
  function amendedPosition(command:RequestCommand){const h=deskPanel.shown();return command.path.endsWith('/amend')&&h?.tradeId===command.body.tradeId?h:undefined;}
  // The panel returns to the trade at the revision the new read holds, on its terms, at the binding the amendment produced, which is
  // another when it changed the trade's instrument. A new read closes the panel, so what it cannot find again it says it closed.
  async function follow(h:Holding|undefined,receipt?:Receipt){
    if(!h||!holdings)return;
    const found=followedPosition(holdings.holdings,h,receipt);
    if(found.index!==null)await deskPanel.open(found.index,'terms');
    if(found.note)options.notice(found.note);
  }
  // A new trade, once booked or found committed, is shown as the position it is: its row is marked in the grid and the panel opens on it, so the
  // person sees what was booked. Where the read has no row to open for it, in a pivot or beyond the books selected, the page says so.
  async function followBooked(command:RequestCommand,receipt:Receipt){
    if(!command.path.endsWith('/book')||!holdings)return;
    if(view.mode!=='trades'){options.notice('The new trade is booked. This Blotter shows totals, which hold no rows to open: set Display to Individual trades to see it.');return;}
    const found=bookedPosition(holdings.holdings,receipt,command.path.startsWith('swaps/')?((command.body as {scheduled?:{factor?:unknown}}).scheduled?.factor?'heat_rate':'financial_swap'):'future');
    if(found.index!==null)await deskPanel.open(found.index);
    if(found.note)options.notice(found.note);
  }
  // A retained settlement command is retried or checked with the settlement view it came from left open, as its outcome reads the
  // Blotter again as a live correction does. Any other retained request is settled by a new read, which closes the panel, so the
  // panel asks about its edits first.
  function retainedAction(work:()=>Promise<void>){if(pending.command&&isLifecycleCommand(pending.command.path)){if(!busy())void act(work);}else asked(work);}
  get('futures-retry').onclick=()=>retainedAction(async()=>{if(!pending.command)throw Error('No retained booking to retry.');await send(pending.command);});
  get('futures-reconcile').onclick=()=>retainedAction(async()=>{
    if(!pending.command)throw Error('No retained booking to reconcile.');
    try{
      const result=await api<Receipt>(tradeStatusPath(pending.command.path),{operationId:pending.command.operationId});
      if(!result.tradeId)throw Error('No committed outcome is visible yet. Retry the original request with the same identity.');
      const command=pending.command,amended=amendedPosition(command);
      pending.clear();uncertain=false;store();
      if(isLifecycleCommand(command.path)){await settled(command);return;}
      await load();
      await follow(amended,result);await followBooked(command,result);
    }catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}
  });
  get('futures-new').onclick=()=>void openNewTrade();
  field('blotter-mode').onchange=()=>{if(busy())return;get('blotter-pivot-fields').hidden=field('blotter-mode').value!=='pivot';};
  // Grouping arranges the rows already read; every grouping's subtotals came with them. A grouping the read cannot show is
  // refused in the banner, and the grouping on screen stays.
  // The control shows the grouping the Blotter shows, also after one it refused.
  field('blotter-row-group').onchange=()=>void perform({kind:'groupRows',rowGroup:(field('blotter-row-group').value||null) as RowGroup}).then(()=>{field('blotter-row-group').value=view.rowGroup??'';});
  get('blotter-apply').onclick=()=>asked(load);
  get('blotter-save').onclick=()=>asked(async()=>{
    const draft=readView();const next=parseBlotterViews(JSON.stringify([{...draft,name:field('blotter-name').value}]))[0];
    const saved=await layoutApi<{version:string;layout:BlotterView}>('table-views/blotter',{layout:next,expectedVersion:draftBase?.name===next.name?draftBase.version:versions.get(next.name)??null});
    versions.set(next.name,saved.version);draftBase={name:next.name,version:saved.version};savedViews=[...savedViews.filter(v=>v.name!==next.name),saved.layout];earlierViews=earlierViews.filter(name=>name!==next.name);
    view=structuredClone(saved.layout);showPanes();viewControls();field('blotter-saved').value=next.name;text('blotter-view-note','Saved to your account. Dates and market inputs are selected separately.');table=undefined;workspace.closeMenus();await load();
  });
  // An earlier-format view is named so it can be saved again under its name, and explained as a notice, not an error.
  // A saved view the person chooses becomes the view the Blotter reads, the base of its next save.
  function useSaved(saved:BlotterView){draftBase={name:saved.name,version:versions.get(saved.name)!};table=undefined;view=structuredClone(saved);showPanes();field('blotter-name').value=view.name;field('blotter-saved').value=view.name;viewControls();}
  // Choosing the unsaved current view is no choice: the list shows the view the Blotter shows again.
  field('blotter-saved').onchange=()=>{const name=field('blotter-saved').value;if(name)void perform({kind:'selectView',name});else field('blotter-saved').value=draftBase?.name??'';};
  get('blotter-views-reload').onclick=()=>asked(async()=>{
    const name=field('blotter-saved').value;
    await loadViews();
    const saved=savedViews.find(v=>v.name===name);
    if(saved){useSaved(saved);await load();}
    text('blotter-view-note',saved?'The selected account layout has been reloaded.':'Saved-view list refreshed. Choose a view to restore it.');
  });
  get('blotter-delete').onclick=()=>void act(async()=>{const name=field('blotter-saved').value;if(!name)throw Error('Choose a saved view to delete.');await layoutApi('table-views/blotter/delete',{name,expectedVersion:draftBase?.name===name?draftBase.version:versions.get(name)});if(draftBase?.name===name)draftBase=undefined;savedViews=savedViews.filter(v=>v.name!==name);earlierViews=earlierViews.filter(n=>n!==name);versions.delete(name);viewControls();field('blotter-name').value='';text('blotter-view-note','Saved view deleted. The displayed data is unchanged.');});
  // A trade-row export states the trade fields of the rows the filters show, the desk's own and the added columns, exactly.
  function tradeExport(grid:ReturnType<typeof renderDataGrid>):{headers:string[];rows:string[][];numeric:number[]}{
    const read=holdings!,columns=tradeColumns.filter(([key])=>(deskFields as readonly string[]).includes(key)||key==='currency'||view.columns.includes(key));
    const rows=grid.shownRows().map(i=>{const h=read.holdings[i],fields=tradeFields(h);return columns.map(([key])=>notValued(h,key)??noted(drawn?.rows[i],key)??fields[key]??'');});
    return {headers:columns.map(c=>c[1]),rows,numeric:columns.flatMap(([key],i)=>numericFields.includes(key)?[i]:[])};
  }
  get('blotter-export').onclick=()=>{if(busy()||!(table||ladderTable)||!holdings||!lastReadRequest)return;const grid=(table??ladderTable)!,filterKeys=table?tableColumns:ladderShown?.headers??[];const pivot=view.mode==='pivot';const metadata=[['Blotter snapshot'],['As of',holdings.asOf],['Knowledge cutoff',holdings.context.knownAt],['Convention',holdings.convention],['Display',view.mode],['Grouping',pivot?view.groupBy.join(' / '):rowGroupLabel(view.rowGroup)],['Measures',pivot?view.measures.join(' / '):'Trade rows'],['Portfolios',view.portfolioIds.length?catalog!.books.filter(b=>view.portfolioIds.includes(b.portfolio_id)).map(b=>b.portfolio).filter((v,i,a)=>a.indexOf(v)===i).join(' / '):'All authorized portfolios'],['Books',view.bookIds.length?catalog!.books.filter(b=>view.bookIds.includes(b.id)).map(b=>b.name).join(' / '):'All books in selected portfolios'],['Search',grid.state().query],['Column filters',JSON.stringify(Object.fromEntries(grid.state().columns.flatMap((v,i)=>v===null?[]:[[filterKeys[i],v]])))],['Numerical policy',holdings.pivotNumericalPolicy],['Pivot convention',pivot?'trading_blotter_totals_v1':'Not applicable'],['Positions not valued at this close',String(excludedPositions(holdings.unvaluedPositions))],[]];
    // The Ladder lens exports the ladder's rows the filters show, exactly, with the run they come from (D230).
    const ladder=ladderTable&&ladderShown?{...ladderShown,rows:ladderTable.shownRows().map(i=>ladderShown!.rows[i])}:undefined;
    const shown=ladder??(pivot?{headers:table!.headers(),rows:table!.visibleRows(),numeric:table!.numeric()}:tradeExport(table!));
    const about=ladder?[...metadata.slice(0,-1),['Risk run',get('desk-run-title').textContent??''],['Run detail',get('desk-run-detail').textContent??''],[]]:metadata;
    const csv=guardedCsv(about)+guardedCsv([shown.headers,...shown.rows],shown.numeric);void act(async()=>{try{await api('trading/blotter',lastReadRequest);}catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}downloadCsv(ladder?'leanetrm-delta-ladder.csv':'leanetrm-blotter.csv',csv);});
  };
  get('futures-refresh').onclick=get('blotter-retry').onclick=()=>asked(()=>load('directory',false,true));
  // A Blotter view a navigation target names, applied as Apply applies the view's controls.
  async function show(selection:BlotterSelection){
    restore();if(!viewsLoaded)await loadViews();
    const next:BlotterView={...structuredClone(view),mode:selection.mode,bookIds:[...selection.bookIds],portfolioIds:[...selection.portfolioIds],
      groupBy:[...selection.groupBy],splitBy:[],measures:[...selection.measures]};
    if(view.mode!==next.mode||JSON.stringify([view.groupBy,view.splitBy,view.measures])!==JSON.stringify([next.groupBy,next.splitBy,next.measures]))relayout(next);
    view=next;viewControls();await load();
  }
  // Every Blotter action is an operation (blotter-operations.ts), which the toolbar, keys, panes and menus dispatch here, as the assistant
  // will: each kind has the one handler below, and none runs while the page is busy. Sorting and filtering in the grid's headers, moving,
  // resizing and putting panes away in the dock, and the details' own collapse belong to those components, which tell the view (onLayout,
  // changed, collapsed).
  const operate=createBlotterOperations({
    selectView:async({name})=>guarded(async()=>{
      if(earlierViews.includes(name)){field('blotter-name').value=name;options.notice(earlierFormatNote(name));return;}
      const saved=savedViews.find(v=>v.name===name);if(!saved)throw refuse(`no saved view is named ${name}`);
      useSaved(saved);workspace.closeMenus();await load();
    },()=>{field('blotter-saved').value=draftBase?.name??'';}),
    // The books and portfolios are those the scope lists offer once the person has said what the details held may go; the Blotter is read
    // again for them, its other controls as they stand. One no longer listed is refused, never dropped.
    chooseScope:async({bookIds,portfolioIds})=>guarded(async()=>{
      const scope=[['blotter-books',bookIds],['blotter-portfolios',portfolioIds]] as const;
      for(const [id,ids] of scope)if(ids.some(chosen=>!Array.from(get<HTMLSelectElement>(id).options,o=>o.value).includes(chosen)))throw refuse('a book or portfolio is not one the Blotter lists');
      for(const [id,ids] of scope)for(const option of get<HTMLSelectElement>(id).options)option.selected=ids.includes(option.value);
      await load('directory',false,true);
    }),
    // Grouping arranges the rows already read; every grouping's subtotals came with them. A grouping the read cannot show is refused in the
    // banner, and the grouping on screen stays.
    groupRows:async({rowGroup})=>acting(act,async()=>{
      const shown=view.rowGroup;captureLayout();view.rowGroup=rowGroup;field('blotter-row-group').value=rowGroup??'';
      try{render();}catch(error){view.rowGroup=shown;field('blotter-row-group').value=shown??'';throw error;}
    }),
    sort:async({sort})=>acting(act,async()=>{rowColumns(sort.map(s=>s.key));captureLayout();view.grid={...view.grid,sort};render();}),
    filter:async({query,columns})=>acting(act,async()=>{rowColumns(Object.keys(columns));captureLayout();view.query=query;view.columnFilters={...columns};render();}),
    chooseColumns:async({columns})=>{requireModeColumns(view,columns);captureLayout();view.columns=columns;render();},
    // A position opens only from the read on screen; no position closes the details that show one, asking first about their edits.
    selectPosition:async({key},{placed})=>{
      if(key===null){if(panel.current()?.kind==='position')await detailsPane.collapse();return panel.current()?.kind!=='position';}
      if(!holdings){notOpened();return false;}
      const index=holdings.holdings.findIndex(h=>rowKey(h)===key);
      if(index<0||!positionInput(index))throw refuse('that position is not among the rows on screen');
      return deskPanel.open(index,undefined,!placed);
    },
    details:async({open})=>showDetails(open),
    arrange:async({panes})=>{
      if(panes.collapsed.includes('details')!==dock.arrangement().collapsed.includes('details'))throw refuse('the details open and close with what they show');
      chartsMadeWay=false;view.panes=panes;dock.set(panes);requestAnimationFrame(fitRoom);
    },
    collapsePane:async({pane,collapsed})=>{if(pane==='details')await showDetails(!collapsed);else if(collapsed)dock.collapse(pane);else dock.reveal(pane);},
    // The default arrangement has the details collapsed: details that show something collapse first, as ⌄ collapses them, asking about their
    // edits, which kept keep the layout as it is; and a page busy before or after the question is refused (guard), as every change is.
    resetLayout:async()=>guard({busy,act,leave:async()=>{if(panel.current()!==null)await detailsPane.collapse();return panel.current()===null;}},
      async()=>{chartsMadeWay=false;view.panes=defaultBlotterArrangement();dock.set(view.panes);}),
    // The Blotter pops out into a window of its own with the view it shows, its unsaved edits among them, once the person has said what
    // the details hold may go.
    popOut:async()=>{
      const own=tie;if(!own)throw refuse('the Blotter is already in a window of its own');
      await guarded(async()=>{retainedStays('opens in a window of its own');own.open(handOff());away();});
    },
    // The main window closes the pop-out, which hands back its view as it goes; the pop-out closes itself.
    bringBack:async()=>{
      const shown=options.window;
      // The pop-out's own requests go back with it (handOver), which its window does as it closes.
      if(shown.kind==='popout'){await guarded(async()=>{shown.close();});return;}
      const own=tie;if(!own?.out())throw refuse('the Blotter is not in a window of its own');
      if(!await leaveWindow())return;
      await guarded(()=>own.bringBack());
    },
    draftCloseOut:async({key})=>draftCloseOut(key),
    // A position is amended from its details, for the revision they read: a PPA, an account that cannot amend or a revision that cannot be
    // amended is refused with why.
    amend:async({key})=>{
      const h=holdings?.holdings.find(h=>rowKey(h)===key),shown=deskPanel.shown();
      if(!h)throw refuse('that position is not among the rows on screen');
      if(!shown||rowKey(shown)!==key)throw refuse('a position is amended from its details, which do not show it');
      const why=amendRefusal(h.instrumentType,!!catalog?.canAmendTrades,position.amendState(h));if(why)throw Error(why);
      await acting(act,()=>h.instrumentType!=='future'?swapEditor.open(h,Number(h.revision)):editor.open(h,Number(h.revision)));
    }},{busy});
  // The person's own actions: each takes away what an earlier refusal said, and one refused, before its handler or by it, says why as any
  // failed action does, and shows no position.
  function perform<O extends BlotterOperation>(operation:O,caller:BlotterCaller={placed:false}){
    options.failureCleared();
    // A refusal the page has already said where it applies, as the details say a refused close-out, is not said again in the banner.
    return operate(operation,caller).catch(error=>{if((error as {shown?:boolean}).shown!==true)options.failed(error);return undefined;});
  }
  // Sorting and filtering name columns of the rows on screen, never one a read shows for itself alone.
  function rowColumns(keys:readonly string[]){
    if(!holdings||!table)throw refuse('the Blotter shows no rows to sort or filter');
    const other=keys.find(key=>!tableColumns.includes(key)||transientKeys.has(key));
    if(other!==undefined)throw refuse(`${other} is not a column of the rows shown`);
  }
  // The details open while they show something, a position, a new trade, the risk report or a ladder contract, and are never open empty;
  // closing them asks first about their edits.
  async function showDetails(open:boolean){
    if(!open){if(panel.current()!==null)await detailsPane.collapse();return;}
    if(panel.current()===null)throw refuse('the details open on what they show, and show nothing');
    panel.reveal();
  }
  // The trade the panel shows, as the page reports it: its position, at the revision the Blotter holds, or at the one its form shows
  // while its form is open, which can be an earlier one.
  function shownTrade():TradeShown|null{
    const h=deskPanel.shown();
    if(!h)return null;
    const form=editor.displayedRevision()??swapEditor.displayedRevision();
    return {tradeId:h.tradeId,instrumentType:h.instrumentType as TradeShown['instrumentType'],revision:form??Number(h.revision),reference:h.tradeReference};
  }
  // The new trade the panel holds, as the page reports it: of its product, or of none until the person has chosen one.
  function newTradeShown():EntryShown|null{const mode=panel.current();return mode?.kind==='new'?{product:mode.product}:null;}
  setUp=true;
  // A main window that reloaded is handed a pop-out's requests once it is back, after its own first read: they are taken, and shown, as
  // soon as they arrive.
  if(options.window.kind==='main')setInterval(()=>{if(!busy()&&owner&&owner===options.profile()&&!tie?.out()&&takeStoredRequest())sync();},1000);
  return {load:({background=true}:{background?:boolean}={})=>load('directory',false,background),reset,sync,show,
    /** The values of the read under way, which a read in the background draws after `load` returns: rejects with the read's failure. */
    valued:()=>pricingRead,
    // The person's saved views, read for the sidebar when they are not read yet.
    async views(){restore();if(!viewsLoaded)await loadViews();},
    // Shows the saved view a sidebar entry names, read afresh with the other saved views; the page then reads the Blotter.
    async openView(name:string){
      restore();await loadViews();
      const saved=savedViews.find(v=>v.name===name);if(!saved)throw Error(`The saved view ${name} is no longer saved to your account.`);
      useSaved(saved);
    },
    // A live correction reads the desk again, a position or new trade in the panel with it unless the person has edits in it, and the risk
    // report at the corrected inputs, carrying a setup's choices over; it clears a risk run with a note. No live view can book, so no form of
    // a new trade is held while corrections arrive.
    async liveRefresh(){
      const plan=liveRefreshPlan({panel:panel.current(),unsaved:editing(),riskRun:!!risk.current(),riskRetained:risk.unconfirmed()});
      inputsChanged(plan.risk);
      await load(catalog?.kind??'directory',true);
      if(plan.risk.reopen)await risk.open();
      if(plan.reloadPanel){await position.reload();await swapEditor.liveRefresh();}
    },
    openProduct:(product:NewProduct)=>chooseProduct(product,false,true),
    async bookedExternal(tradeId:string){
      try{
        await load();
        const rows=holdings?.holdings??[],matches=rows.map((h,index)=>({h,index})).filter(x=>x.h.tradeId===tradeId);
        if(view.mode==='trades'&&matches.length===1)await deskPanel.open(matches[0].index,'terms');
        else options.notice('Trade saved. Select its position binding in the Blotter to inspect it.');
      }catch(error){options.notice('The trade was saved. Refresh the Blotter to inspect its position.');options.failed(error);}
    },
    refreshDisplay(){chartState=undefined;drawn=undefined;clearChart(get('blotter-chart'));get('blotter-chart-card').hidden=true;risk.refresh();booking.refresh();editor.refresh();swapEditor.refresh();crudeOperations.sync();captureFilters();render();},
    refresh(){risk.refresh();booking.refresh();editor.refresh();swapEditor.refresh();crudeOperations.refresh();captureFilters();render();},
    // Opens a trade a target names, as the position it is in at the revision the Blotter holds. An earlier revision is said not to be shown.
    async openTrade(tradeId:string,instrumentType:TradeShown['instrumentType'],revision:number|null){
      await pricingRead;
      requireTradeRows(view.mode);
      if(!holdings)throw Error('Choose a market date to read the Blotter, then open the trade.');
      const index=tradeIndex(holdings.holdings,{tradeId,instrumentType}),h=holdings.holdings[index];
      await deskPanel.open(index,'terms');
      const note=revisionNote(h.tradeReference,revision,Number(h.revision));
      if(note)options.notice(note);
    },
    // What a target for a trade needs of the Blotter, checked before the page moves for it, against the view its last read applied and
    // not a Display choice not yet applied: the person's view is never changed to fit it.
    requireTradeRows:()=>requireTradeRows(view.mode),
    // What the Blotter shows: its risk report, the trade in its panel, the new trade entered there, or the view its last read applied.
    // While the Blotter is popped out, that is what its window last reported; before it has, the main window cannot say.
    report(){
      if(tie?.out()){
        const shown=tie.shown();if(!shown)throw Error('The Blotter’s window has not said what it shows yet.');
        const {page,...state}=shown;return state;
      }
      return {risk:panel.current()?.kind==='risk',trade:shownTrade(),entry:newTradeShown(),lens:lensShown()||ladderOn()?'risk' as const:'values' as const,
        view:{mode:view.mode,bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds],groupBy:[...view.groupBy],measures:[...view.measures]},
        // A pane is open as the dock draws it (BlotterLayoutShown).
        pane:{details:dock.showing('details'),charts:dock.showing('charts')},poppedOut:options.window.kind==='popout'};
    },
    // A form of the panel with edits not yet sent, the risk report's setup among them, or a trade request or risk job whose outcome is unconfirmed.
    unsaved:()=>!!pending.command||options.tickets.pending().length>0||editing()||risk.unconfirmed(),
    // Whether the panel holds the form of an amendment or of a new trade, or a risk report's setup, with edits, and the question asked before it is given up: false means the person kept it. The forms
    // are dropped without a question when the page is left on the person's word, as the assistant's Go anyway is.
    editing,leave,dropForms,cancelRead,
    // A page exit has already passed its unsaved-edit guard. Clear the details as well as forms, without returning focus to a row.
    leavePage,
    // The Blotter popped out of the main window (blotter-workbench.md, Pop-out Blotter): whether it is, bringing its window to the front,
    // sending it the context the header selects now, and ending it, saying why, when the main window signs out, changes account or closes.
    poppedOut:()=>!!tie?.out(),front:()=>tie?.front(),follow(){if(!busy())try{tie?.follow();}catch(error){options.failed(error);}},
    ended(reason:EndReason){tie?.end(reason);placeWindow();},
    // Whether the pop-out holds edits, and the question asked before a change gives them up.
    windowEdits,leaveWindow,
    // In the pop-out: the view the main window handed over, the view to hand back, the request to hand over, and the retained request kept
    // alone, with no values, when there is no main window to hand it to.
    adopt,handOff,handOver,handOverRefusal,retained:()=>!!pending.command||risk.unconfirmed(),
    // Which unconfirmed requests the pop-out keeps, as it says what it can do with each.
    retainedKinds,
    // The catalog last read says whether the account may book or amend, which Retry is offered by; it holds no values, and the backend
    // authorizes the retry again.
    keepOnlyRetained(){const authority=catalog;leavePage();clear();catalog=authority;retainedOnly=true;get('futures-page').dataset.retainedOnly='';sync();}};
}
