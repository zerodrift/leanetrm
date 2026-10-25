import {amountText,prepareAmountDisplay} from './amount-display.js';
import {type PartyCatalog,type Trader} from './trade-annotations.js';
import {setupSwapEditor,readSwapView} from './swap-editor.js';
import {setupTradeEditor,readTradeView} from './trade-editor.js';
import {type FuturesContract as Contract} from './futures-contract.js';
import {PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {releasesRetained,retainedText,retryAllowed} from './trade-requests.js';
import {setupRisk,riskDenied,type RunView} from './risk-view.js';
import {changeChart,changeScales,largestMove,lensCells,positionKey,runBar,runExclusions,runSummary,type LensCell} from './risk-lens.js';
import type {ExcludedPosition,RiskReport} from './risk-report.js';
import {setupFuturesBooking} from './futures-booking.js';
import {draftShapes,otherRevision,readCurveShapes} from './curve-shapes.js';
import {newTradeControl,newTradeProducts,setupNewTrade,type NewProduct} from './new-trade.js';
import {createTableWorkspace} from './table-workspace.js';
import {renderDataGrid} from './data-grid.js';
import {pivotMatrix,pivotChartPanels,groupLabel,type PivotGroup} from './analytics-model.js';
import {renderChart,clearChart,type ChartSpec} from './chart-view.js';
import type {BlotterSelection,EntryShown,TradeShown} from './assistant-pages.js';
import {isPriceMeasure,priceMeanPolicy,blotterColumns,deskFields,groupChoices,measureChoices,newBlotterView,parseBlotterViews,pivotColumns,readSavedViews,rowGroups,withoutColumns,type BlotterView,type RowGroup} from './blotter-layout.js';
import {guardedCsv,downloadCsv} from './csv-export.js';
import {friendly,publicText,displayInstant,deliveryLabel,curveName} from './market-view.js';
import {valuationReason,valuationNote,excludedPositions,valuationMeasure} from './valuation-status.js';
import {commodityColors,deskGroups,deskSummaryViews,holdingFormats,positionChartPanels,positionRows,positionsTile,priceUnit,projectsFromSeveralCurves,pvChartPanels,severalCurvesShape,signOf,signedText,summaryTiles,valueFields,type CurveShape,type DeskRow,type Holding,type Summary} from './desk-model.js';
import {askInPanel,askLeave,liveRefreshPlan,panelPlacement,riskRefreshPlan,sameMode,setupSidePanel,type PanelMode,type RiskRefresh} from './side-panel.js';
import {setupPositionPanel,type PositionInput,type PositionRisk} from './position-panel.js';
import {bookedPosition,followedPosition,requireTradeRows,revisionNote,setupDeskPanel,tradeIndex} from './desk-panel.js';
import {clearDeskCharts,deskCells,deskColumns,groupHeader,renderDeskCharts,renderMoneyTile,renderPositionsTile,renderRiskTile,renderRunBar,riskColumns,riskGroupHeader,type LensRowView,type RiskTile,type RiskTileResults} from './desk-view.js';

type Context={contextId:string;effectiveOn:string;knownAt:string};
type ReadRequest={snapshotId:string;convention:string;knowledgeMode:'current'|'retained';contextId?:string};
type BookingCatalog='directory'|'priced_contracts';
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string;portfolio_id:string}[];context:Context;asOf:string;canBook:boolean;canAmendTrades:boolean;bookingUnavailableReason:string|null}&({kind:'directory'}|{kind:'priced_contracts';contracts:Contract[]});
type Holdings={convention:string;pivotNumericalPolicy:string;groups:PivotGroup[];context:Context;asOf:string;evaluatedContracts:number;hasSwaps:boolean;holdings:Holding[];totals:{book:string;currency:string;executionPnl:string|null;unvaluedPositions:number}[];unvaluedPositions:number};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
// A stored view an earlier release saved is listed with this label. It cannot be applied, and choosing it says why.
const earlierFormatLabel='Earlier format · save again';
const earlierFormatNote=(name:string)=>`Saved view "${name}" is in an earlier format and cannot be applied. Set up the Blotter as you want it and save it again under this name, or delete it.`;
const curveColumn=deskColumns.findIndex(([key])=>key==='curve'),markColumn=deskColumns.findIndex(([key])=>key==='mark');
// Under the Risk lens a docked panel takes the grid's width from the shock sizes first, which a row's panel also states, and then its deltas.
const riskGiveUp=['riskShock','riskDelta'].map(key=>riskColumns.findIndex(([column])=>column===key));
// The Risk lens columns, which a saved view never keeps.
const riskKeys=riskColumns.flatMap(([key])=>key.startsWith('risk')?[key]:[]);
// What a new read of the Blotter says of the risk run it clears (D220): changing the close, the context or the book scope says so in the
// design's words; any other new read, a Refresh, another Display or grouping, or the Blotter opened again, says it was read again, each
// read at current knowledge being made at a context of its own.
const readNotes={inputs:'Risk results cleared · the close or books changed',read:'Risk results cleared · the Blotter was read again'} as const;
type ReadCause=keyof typeof readNotes;
// The knowledge a read is made at, as the run bar says a run made on it was: working data, a historical view or live corrections.
const knowledgeWords={working:'current knowledge',historical:'historical view',live:'live corrections'} as const;
// What the risk tile calls the report a run was set up as; a retained run does not record one.
const tileReports={held:'every held contract',parallel:'every held curve',custom:'custom scenario'} as const;
/**
 * A run's results as the desk shows them inline (D220), by the key of the results they are: made at another market context than the
 * read on screen, refused with why, or shown, with each position's cell, what the run leaves out and its summary.
 */
type Inline={key:string;state:'elsewhere'}|{key:string;state:'failed';reason:string}
  |{key:string;state:'shown';run:number;cells:Map<string,LensCell>;excluded:ExcludedPosition[];summary:ReturnType<typeof runSummary>;scales:Map<string,string>;missing:number;parts:number};
// How the Group by control names each row grouping; a view without one is a flat list.
const rowGroupLabels:Readonly<Record<NonNullable<RowGroup>,string>>={book:'Book',market:'Market',instrumentType:'Instrument type'};
const rowGroupLabel=(group:RowGroup)=>group===null?'None':rowGroupLabels[group];
// The trade fields an export of trade rows states, beside the desk's own: money is in its currency.
const numericFields=['tradeCount','lots','quantity','executionPrice','mark','executionPnl','fixedPrice','presentValue','revision'];
// At most this many curves are shaped in one read (market/curve-shapes).
const curvesPerShapeRead=64;

export function setupFutures(options:{newPpa:()=>Promise<void>;api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRunRisk:()=>boolean;bookingUnavailableReason:()=>string;zone:()=>string;profile:()=>string;onReadContext:(at:string,known:string)=>void;notice:(message:string)=>void;
  // The definition revision of the curves the selected close prices; positions of another revision get no shape from it.
  definitionRevision:()=>string;
  // Whether Curves & prices lists a market, and opens it with one of its contracts' price history shown when a contract is named.
  curveListed:(curve:string)=>boolean;openCurve:(curve:string,contractId:string|null)=>Promise<void>;source:()=>{convention:string;snapshotId:string;context?:{contextId?:string}};
  // What the page reports of itself has changed: the panel moved to another mode, or its form was closed, or the lens changed.
  reported:()=>void;
  // The knowledge the Blotter is read at: working data at current knowledge, a historical view, or live corrections.
  knowledge:()=>keyof typeof knowledgeWords;
  // The panel's form started or stopped holding unsaved edits.
  unsavedChanged:()=>void}){
  const {api,act,busy}=options;
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
  const workspace=createTableWorkspace(get('blotter-workspace'),{
    key:'blotter',rowMode:'trades',rowLabel:'Individual trades',dimensions:groupChoices,measures:measureChoices,
    pivotHelp:'Totals keep instrument types, quantity specifications and currencies separate. Blank measures are unavailable. Lot totals require dated-contract grouping. Price measures keep quote multipliers separate; averages use 18 decimal places, ties to even. Changing display mode clears row filters.',
    scopes:[['portfolios','Portfolios'],['books','Books']],actions:[['risk','Risk report'],['refresh','Refresh'],['export','Export'],['new','New trade']],onApply:()=>guarded(load),
    note:get('new-trade-availability'),lead:get('desk'),overview:get('desk-charts'),aside:get('side-panel'),rowGroups:rowGroups.map(group=>[group,rowGroupLabels[group]] as const),lens:get('desk-lens'),
  });
  // Existing booking controls share the table toolbar.
  get('blotter-refresh').id='futures-refresh';get('blotter-new').id='futures-new';get('blotter-rows').id='desk-positions';
  let catalog:Catalog|undefined,holdings:Holdings|undefined,summaries:Summary[]|undefined,owner='',snapshot='',uncertain=false,generation=0;
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
  // The close, the context chosen and the book scope of the read on screen, which tell a read at other inputs from one made again.
  let readInputs:string|undefined;
  let chartState:{panels:ChartSpec[];request:{context:unknown;view:unknown}}|undefined;
  // What the trade rows' desk drew from the read on screen, which the side panel draws its position from.
  let drawn:{rows:DeskRow[];colors:ReadonlyMap<string,string>}|undefined;
  let view=newBlotterView(),savedViews:BlotterView[]=[],table:ReturnType<typeof renderDataGrid>|undefined,tableColumns:string[]=[];
  // Columns the current read shows that a saved view never keeps: each pivot group's count of positions it leaves out, and the desk's curve shapes.
  let transientKeys:ReadonlySet<string>=new Set();
  let versions=new Map<string,string>();
  // Stored views an earlier release saved: listed so they can be saved again or deleted, never applied.
  let earlierViews:string[]=[];
  let draftBase:{name:string;version:string}|undefined;
  let viewsLoaded=false;
  // The Values | Risk lens of the trade rows (D220), the run's results it shows once they are prepared, the key of the results being
  // prepared, and the run the lens last turned to Risk for: a run's results turn it once, and the person turns it after that.
  let lens:'values'|'risk'='values',inline:Inline|undefined,preparing:string|undefined,lensFor:number|undefined;
  // Each report of the run on screen by a number of its own, so that results that are new can be told from those already shown.
  const reportIds=new WeakMap<RiskReport,number>();let reports=0;
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
  // The Values | Risk lens of the trade rows (D220): Values shows the positions' values and their long and short sizes, Risk the run's results.
  for(const button of get('desk-lens').querySelectorAll<HTMLButtonElement>('[data-lens]'))
    button.onclick=()=>{const next=button.dataset.lens as typeof lens;if(busy()||next===lens||!inline||inline.state!=='shown')return;lens=next;captureFilters();render();options.reported();};
  // Shows the lens, with the one chosen, where it applies: the trade rows of a read whose run's results are on screen.
  function syncLens(){
    const control=get('desk-lens');control.hidden=!(inline?.state==='shown'&&view.mode==='trades'&&holdings);
    for(const button of control.querySelectorAll<HTMLButtonElement>('[data-lens]')){button.setAttribute('aria-pressed',String(button.dataset.lens===lens));button.disabled=busy();}
  }
  function readContext(){const source=options.source();return {snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})};}
  const editor=setupTradeEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),leave:()=>panel.leave(),unsavedChanged:options.unsavedChanged,
    queue:async command=>{if(pending.command)throw Error('Reconcile the retained request before submitting another command.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});

  const swapEditor=setupSwapEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),leave:()=>panel.leave(),unsavedChanged:options.unsavedChanged,shape:market=>drafts.shape(market),
    ask:question=>panel.ask(askInPanel(get('side-panel'),question)),
    queue:async command=>{if(pending.command)throw Error('Reconcile the retained request first.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});
  // The form of a new future, which the panel hosts as it hosts the forms of the amendments. What it books is kept as the request it was first sent as.
  const booking=setupFuturesBooking({api,act,busy,zone:options.zone,context:readContext,catalog:()=>catalog,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),unsavedChanged:options.unsavedChanged,shape:market=>drafts.shape(market),
    book:async body=>{const command=pending.begin('futures/book',body,undefined,'New futures trade');store();sync();await send(command);}});
  // The side panel beside the positions: a position's curve, terms, risk and history, and the form of its amendment. Opening a row
  // opens it; Amend puts the amendment's form in the Terms tab, and a new trade has the whole panel, its form beneath a Future | Swap switch.
  const main=document.querySelector('main')!,phone=window.matchMedia('(max-width:760px)');
  const editing=()=>editor.unsaved()||swapEditor.unsaved()||booking.unsaved()||risk.edited();
  const docked=()=>panelPlacement(main.clientWidth,document.body.classList.contains('assistant-open'))==='docked';
  // A docked panel takes the grid's width, so the grid gives up the columns the panel shows for its position, Curve first and then
  // Mark, for as long as its columns do not fit the room it has left.
  const dockedGiveUp=()=>panel.current()!==null&&docked()?lensShown()?riskGiveUp:[curveColumn,markColumn]:[];
  const wide=()=>!phone.matches&&swapEditor.settlementOpen();
  const panel=setupSidePanel(get('side-panel'),{
    // What the panel holds that switching would lose: the form of an amendment or of a new trade, with edits in it.
    unsaved:editing,confirmLeave:askLeave(get('side-panel')),docked,
    // On a phone the panel is a sheet over the page, which stays out of reach while it is open, unless the Blotter has been left.
    sheet:()=>phone.matches&&!get('futures-page').hidden,
    // A swap's settlement figures need more room than a docked panel has: off a phone, the panel lies over the desk at a wider width.
    wide,
    changed:()=>{table?.fitColumns(dockedGiveUp());modeChanged();placeRetained();},closed:()=>deskPanel.closed()});
  // A request whose outcome is not known is kept with its Check status and Retry in a card, which is in the panel while the panel is open,
  // whatever it shows, and on the page when it is not, so that nothing leaves the person with no way to find out what became of it.
  const retained=get('futures-pending');
  function placeRetained(){const slot=get(panel.current()!==null?'side-panel-retained':'futures-retained');if(retained.parentElement!==slot)slot.append(retained);}
  // The head of the panel's New trade mode, which asks the page for the product the person chooses and for the PPA page.
  const entry=setupNewTrade(panel,{choose:product=>void chooseProduct(product),ppa:()=>guarded(options.newPpa),busy});
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
    renderRisk();options.reported();
  }
  // The panel docks or overlays by the width the main area has: it is placed again when that changes, not when only its height does.
  let mainWidth=main.clientWidth;
  new ResizeObserver(()=>{if(main.clientWidth!==mainWidth){mainWidth=main.clientWidth;panel.place();}}).observe(main);
  new MutationObserver(()=>panel.place()).observe(document.body,{attributes:true,attributeFilter:['class']});
  const position=setupPositionPanel(panel,{zone:options.zone,busy,canAmend:()=>!!catalog?.canAmendTrades,curveListed:options.curveListed,context:()=>JSON.stringify(readContext()),onDenied:clear,
    read:(h,formats)=>h.instrumentType==='financial_swap'?readSwapView(api,readContext(),h,formats,{busy,act,settle:()=>settle(h)}):readTradeView(api,readContext(),h,formats),
    amendment:{
      open:h=>void act(()=>h.instrumentType==='financial_swap'?swapEditor.open(h,Number(h.revision)):editor.open(h,Number(h.revision))),
      // A future's form is open for its binding, a swap's for its instrument.
      shown:h=>{const future=editor.shown(),swap=swapEditor.shown();return !!future&&future.tradeId===h.tradeId&&future.bindingId===h.bindingId||!!swap&&swap.tradeId===h.tradeId&&swap.contractId===h.contractId;},
      close:async()=>{await leave();},
    },
    openCurve:(curve,contractId)=>guarded(()=>options.openCurve(curve,contractId))});
  const deskPanel=setupDeskPanel({shell:panel,position,holdings:()=>holdings?.holdings,input:positionInput,grid:()=>table,notice:options.notice});
  // Gives the forms the panel hosts up, without asking, and places the panel again for what it then shows: a panel left by its settlement
  // view, however it is left, a switch of mode among the ways, has its own width again.
  function resetForms(){editor.reset();swapEditor.reset();booking.reset();risk.dropEdits();panel.place();}
  // Gives the panel's forms up without asking, for a page that is left on the person's word, so that no form stays held that they cannot return to.
  // A new trade has nothing but its form, so the panel it was in is closed with it.
  function dropForms(){resetForms();closeNew();options.reported();}
  function closeNew(){if(panel.current()?.kind==='new')panel.reset();}
  // Asks whether to give up what the panel holds unsaved, when it holds any, and closes its form once the person agrees: false means they
  // kept it. The panel asks for a switch of its own mode; this is the question every other way out of the form asks too.
  async function leave(){if(!await panel.leave())return false;dropForms();return true;}
  // An action of the person's that gives the panel's form up, by reading the Blotter again, which closes the panel, or by leaving it,
  // asks first about the edits the panel holds, before the page is busy: a question asked then would be asked of a page with every
  // control disabled. `kept` hears that the person kept them, for a control that has already changed.
  function guarded(work:()=>Promise<void>,kept?:()=>void){void (async()=>{if(busy())return;if(await leave())await act(work);else kept?.();})();}
  // A swap position's settlement and P&L view (D215) opens from its Terms in the panel's form part, as the form of its amendment does.
  function settle(h:Holding){guarded(()=>swapEditor.openSettlement(h));}
  // New trade (D219) opens the panel on its Future | Swap switch with no product chosen, and reads nothing until the person chooses one.
  // It is offered only where the backend allows booking; the toolbar says why when it does not, so no field opens where booking is impossible.
  // What the panel held is left only when the person says so, which is asked before the page is busy.
  const newTrade:PanelMode={kind:'new',product:null};
  const bookingControl=()=>newTradeControl({catalog,retained:!!pending.command,fallback:options.bookingUnavailableReason()});
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
  // A product is chosen: its form opens once what it needs has been read, in one action with the question asked first. The panel never holds a
  // product it has nothing to show for: when a product's form could not be opened it asks for a product again, with the failure said, whichever
  // product it was, and a page that is busy leaves the panel as it was.
  async function chooseProduct(product:NewProduct){
    const next:PanelMode={kind:'new',product};
    if(busy()||sameMode(panel.current(),next))return;
    if(!await panel.leave())return;
    const at=whereAmI();let ran=false,opened=false;
    await act(async()=>{
      ran=true;
      try{opened=product==='futures'?await openFuture(next):await openSwap(next);}
      catch(error){if(at!==undefined&&whereAmI()===at)await panel.show(newTrade);throw error;}
      if(opened)showForm();else if(sameMode(panel.current(),next))await panel.show(newTrade);
    });
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
  function captureLayout(){captureFilters();if(table?.ready()&&!tableColumns.includes('riskShock'))view.grid=withoutColumns(table.layout(),transientKeys);}
  function readView():BlotterView{
    captureLayout();const next=structuredClone(view);next.mode=field('blotter-mode').value as BlotterView['mode'];
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

  const storageKey=()=>`openetrm.futures.request.server_booking_utc_v1.${options.profile()}`;
  function store(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The current visit still retains the exact in-memory request. */}}
  function restore(){
    if(owner===options.profile())return;
    owner=options.profile();pending.clear();uncertain=false;view=newBlotterView();forgetViews();draftBase=undefined;field('blotter-name').value='';text('blotter-view-note','Named views are saved to your account. Dates and market inputs are selected separately.');get('blotter-column-choices').replaceChildren();text('blotter-table-title','Positions');workspace.closeMenus();viewControls();
    try{const saved=restoreTradeRequest(JSON.parse(sessionStorage.getItem(storageKey())??'null'));if(saved){pending.command=saved;uncertain=true;}}catch{/* An unreadable local draft grants no authority. */}
  }
  // The risk report is the panel's Risk report mode, and the tile says whether it is open, a queued run is under way, or a run's results are
  // on the desk, or at another context. The report tells the page whenever what it shows changes, also when a queued part finishes on its
  // own timer.
  function renderRisk(){
    if(!holdings)return;
    const denial=options.canRunRisk()?null:riskDenied,shown=inline?.state==='shown'?inline:undefined;
    const tile:RiskTile=shown?{state:'results',denial,busy:busy(),results:tileResults(shown)}
      :{state:inline?.state==='elsewhere'?'elsewhere':risk.running()?'running':panel.current()?.kind==='risk'?'open':'none',denial,busy:busy()};
    renderRiskTile(get('desk-tile-risk'),tile,()=>void openRiskReport());
    showRunBar();
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
    return JSON.stringify([run.key,holdings.context.contextId,run.parts.map(p=>p.report?id(p.report):p.failure!==undefined?'failed':'none')]);
  }
  /**
   * A run's results as the desk shows them: not against a read at another market context than the one they were run at, and only once
   * every part says which positions it leaves out, the same ones, and the positions match the read on screen. Their display text is the
   * backend's, in each position's own deal format, prepared before they are shown.
   */
  async function inlineResults(run:RunView,key:string):Promise<Inline>{
    const read=holdings!,parts=run.parts.flatMap(p=>p.report?[p.report]:[]);
    if(run.context?.contextId!==read.context.contextId)return {key,state:'elsewhere'};
    try{
      const excluded=runExclusions(parts),cells=lensCells(parts),held=new Set(read.holdings.map(positionKey));
      if(read.holdings.some(h=>cells.has(positionKey(h))===(h.unavailableReason!==null))||[...cells.keys()].some(k=>!held.has(k)))
        throw Error('The results of this run do not match the positions of the Blotter on screen. Run it again.');
      await prepareAmountDisplay({rows:read.holdings.flatMap(h=>(cells.get(positionKey(h))?.shocks??[]).map(shock=>({amountFormat:h.amountFormat,
        upPnl:shock.up,downPnl:shock.down,basePv:shock.base,upPv:shock.upValue,downPv:shock.downValue,delta:shock.delta,gamma:shock.gamma})))},body=>api<string[]>('amounts/display',body));
      return {key,state:'shown',run:run.key,cells,excluded,summary:runSummary(parts,excluded),scales:changeScales(cells),missing:run.parts.length-parts.length,parts:run.parts.length};
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
  // What the risk tile shows of a run's results: its largest move in each currency, which position it is and why, and each position's own
  // largest move as a small bar, with what the run leaves out and which parts have no results.
  function tileResults(shown:Extract<Inline,{state:'shown'}>):RiskTileResults{
    const held=holdings!.holdings,rows=positionRows(held),at=new Map(held.map((h,i)=>[positionKey(h),i])),run=risk.current();
    const notes=[...(shown.excluded.length?[`Leaves out ${shown.excluded.length} position${shown.excluded.length===1?'':'s'} this close cannot value`]:[]),
      ...(shown.missing?[`${shown.missing} of the run's ${shown.parts} parts ${shown.missing===1?'has':'have'} no results`]:[])];
    return {label:`Risk · ${run?.setup?tileReports[run.setup.report]:'retained run'}`,count:`${shown.summary.factors} shock${shown.summary.factors===1?'':'s'}`,note:notes.length?notes.join(' · '):null,
      rows:shown.summary.largest.map(largest=>{
        const index=at.get(largest.positionKey)!,money=holdingFormats(held[index]).money;
        const bars=held.flatMap(h=>{const key=positionKey(h),cell=shown.cells.get(key),move=cell&&largestMove(key,cell);
          return move&&move.shock.currencyKey===largest.shock.currencyKey?[{value:move.change,title:`${move.tradeReference} · ${signedText(move.change,holdingFormats(h).money)} ${move.shock.currency} if ${move.shock.label} moves ${move.direction}`}]:[];});
        return {currency:largest.currency,text:signedText(largest.change,money),exact:largest.change,sign:signOf(largest.change),
          hint:`Largest move: ${largest.tradeReference} · ${rows[index].market}, if ${largest.shock.label} moves ${largest.direction}`,bars,scale:largest.change.replace(/^-/,'')};
      })};
  }
  // What the Risk lens shows of a position: the shocks that move it, or that nothing moves it; one the run leaves out says so with the
  // reason its own row states, which is why the run leaves it out (D218).
  function lensRow(shown:Extract<Inline,{state:'shown'}>,h:Holding,row:DeskRow):LensRowView{
    const cell=shown.cells.get(positionKey(h));
    if(!cell)return {state:'left',reason:row.unavailable??'Not valued at this close'};
    return cell.shocks.length?{state:'shocks',shocks:cell.shocks,scale:shown.scales.get(cell.shocks[0].currencyKey)}:{state:'unmoved'};
  }
  // What the run on screen says of the position at a row, for its panel's Risk tab.
  function positionRisk(h:Holding,row:DeskRow):PositionRisk{
    if(inline?.state==='elsewhere')return {state:'elsewhere'};
    if(inline?.state==='failed')return {state:'failed',reason:inline.reason};
    if(inline?.state!=='shown')return risk.running()?{state:'running'}:{state:'none'};
    const cell=inline.cells.get(positionKey(h));
    if(!cell)return {state:'left',reason:row.unavailable??'Not valued at this close'};
    return {state:'shown',cell,factors:inline.summary.factors,...(inline.missing?{missing:{parts:inline.missing,of:inline.parts}}:{})};
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
  function sync(){syncLens();booking.sync();risk.sync();editor.sync();swapEditor.sync();position.sync();entry.sync();panel.place();syncRiskReport();workspace.busy(busy());syncNewTrade();retained.hidden=!pending.command;if(pending.command)text('futures-pending-text',retainedText(pending.command));placeRetained();get<HTMLButtonElement>('blotter-export').disabled=busy()||!table?.ready();get<HTMLButtonElement>('futures-retry').disabled=busy()||!pending.command||!retryAllowed(pending.command.path,catalog);get<HTMLButtonElement>('futures-reconcile').disabled=busy();if(view.mode==='pivot')get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(['instrumentType','unit','currency','priceUnit'].includes(n.value))n.disabled=true;});}
  function clearDesk(){get('desk').hidden=true;get('desk-charts').hidden=true;for(const id of ['desk-tile-swapPv','desk-tile-futuresPnl','desk-tile-positions','desk-tile-risk'])get(id).replaceChildren();clearDeskCharts(get('desk-chart-pv'));clearDeskCharts(get('desk-chart-position'));}
  function clear(){generation++;readInputs=undefined;chartState=undefined;drawn=undefined;lens='values';inline=undefined;preparing=undefined;lensFor=undefined;booking.reset();editor.reset();swapEditor.reset();risk.reset();catalog=undefined;holdings=undefined;summaries=undefined;shapes=undefined;drafts.reset();table=undefined;deskPanel.reset();lastReadRequest=undefined;get('blotter-chart').querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(get('blotter-chart'));get('blotter-chart-card').hidden=true;text('header-calculation-time','');get('blotter-books').replaceChildren();get('blotter-portfolios').replaceChildren();workspace.clear();clearDesk();text('futures-summary','Choose a market date to inspect holdings.');sync();}
  // Starts the Blotter afresh: for another close or context, or for the Blotter opened again, each of which says so of a risk run it
  // clears; or, for another account, saying nothing, as everything the page held goes.
  function reset(cause:ReadCause|null){if(cause)replaceRead(cause);else clear();restore();forgetViews();viewControls();snapshot='';get('futures-pending').hidden=true;}
  // A new read of the Blotter replaces the one a run on screen was made at: the run is cleared and the page says why (D220). A live
  // correction and a settlement record say so in their own words.
  function replaceRead(cause:ReadCause){if(risk.shown()||risk.running())options.notice(readNotes[cause]);clear();}
  function choose(id:string,rows:{id:string;name:string}[],selected:string,placeholder='Choose…'){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option(placeholder,''),...rows.map(r=>new Option(publicText(r.name),r.id)));select.value=rows.some(r=>r.id===selected)?selected:'';}
  // A trade row's fields as the trade columns and exports state them: exact amounts, readable names.
  function tradeFields(h:Holding):Record<string,string|null>{
    const {booking,amountFormat,fixedAmountFormat,commodity,quantitySpecification,currencyReference,projectionCurves,...fields}=h;
    return {...fields,priceUnit:priceUnit(h),tradeDate:booking.tradeDate,legalEntity:booking.legalEntity??null,counterparty:booking.counterparty??null,instrumentType:friendly(h.instrumentType),commodity:commodity.name,market:h.marketLabel??curveName(h.curve),contract:deliveryLabel(h.deliveryFrom,h.deliveryTo),executed:displayInstant(h.executedAt,options.zone()),settlement:publicText(h.settlement==='unprocessed'?'Unprocessed':h.settlement)};
  }
  // What a trade row's value cell says when the close cannot value it, on screen and in exports (D218).
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
    // desk on screen whole, and never new tiles and rows beside the old charts. The tiles summarize the read in both display
    // modes; the two charts and grouped positions are the trade-row desk.
    const [swapPv,futuresPnl]=summaryTiles(summaries),mix=positionsTile(summaries,holdings.holdings.length,holdings.unvaluedPositions);
    const plan=pivot?undefined:planDesk(colors);
    // A new future's form in the panel is filled again from the read, keeping what the person chose and typed that it still offers.
    if(booking.mounted())booking.populate();
    const scope=options.profile()+':'+snapshot+':'+catalog.context.contextId;
    get('desk').hidden=false;get('desk-charts').hidden=pivot;
    renderMoneyTile(get('desk-tile-swapPv'),'Swap PV','swap',swapPv,money);
    renderMoneyTile(get('desk-tile-futuresPnl'),'Futures execution P&L','future',futuresPnl,money);
    renderPositionsTile(get('desk-tile-positions'),mix,colors);
    if(plan)renderDesk(scope,colors,plan);else renderPivot(scope);
    text('blotter-table-title',pivot?view.splitBy.length?'Pivot table':'Pivot summary':view.rowGroup===null?'Positions':`Positions · grouped by ${rowGroupLabel(view.rowGroup).toLowerCase()}`);
    workspace.describe(pivot?[
      {label:'Rows',values:view.groupBy.filter(d=>!view.splitBy.includes(d)).map(d=>groupChoices.find(c=>c[0]===d)![1])},
      {label:'Columns',values:view.splitBy.map(d=>groupChoices.find(c=>c[0]===d)![1])},
      {label:'Values',values:view.measures.map(m=>measureChoices.find(c=>c[0]===m)![1])},
    ]:[]);
    text('futures-summary',`${holdings.holdings.length} trade bindings · ${holdings.evaluatedContracts} evaluated futures contract prices. Financial swaps are valued from their side schedules.`);options.onReadContext(holdings.asOf,holdings.context.knownAt);sync();deskPanel.sync();
  }
  // What the trade rows' desk shows, selected from the read: its rows, their trade fields, its group headers and its two charts.
  function planDesk(colors:ReadonlyMap<string,string>){
    const held=holdings!.holdings;
    return {rows:positionRows(held),fields:held.map(tradeFields),groups:deskGroups(view.rowGroup,summaries!,held),
      pvPanels:pvChartPanels(summaries!,colors,money),positionPanels:positionChartPanels(summaries!,held,colors,quantity)};
  }
  function renderDesk(scope:string,colors:ReadonlyMap<string,string>,plan:ReturnType<typeof planDesk>){
    const held=holdings!.holdings,{rows,fields,groups}=plan,shown=lensShown()?inline as Extract<Inline,{state:'shown'}>:undefined;
    // Every other trade column is present, shown when the view chooses it, so a filter on a hidden one stays active. Under the Risk lens
    // the price, mark, value and curve columns give way to the shock, its changes and the delta (D220).
    const details=tradeColumns.filter(([key])=>!(deskFields as readonly string[]).includes(key));
    const head=shown?riskColumns:deskColumns,columns=[...head,...details];
    tableColumns=columns.map(c=>c[0]);transientKeys=new Set(['curve',...riskKeys]);
    const numeric=tableColumns.flatMap((key,i)=>i>=head.length?(numericFields.includes(key)?[i]:[]):['quantity','executionPrice','mark','presentValue','riskUp','riskDown','riskDelta'].includes(key)?[i]:[]);
    const lenses=shown?held.map((h,i)=>lensRow(shown,h,rows[i])):undefined;
    // A lens cell sorts and filters by its exact amount, which only a position one shock moves has; the others by their words.
    const single=(i:number)=>lenses?.[i].state==='shocks'&&lenses[i].shocks.length===1?lenses[i].shocks[0]:undefined;
    const lensWords=(i:number)=>{const lens=lenses![i];return lens.state==='left'?'Left out':lens.state==='unmoved'?'Not moved':lens.shocks.length>1?`${lens.shocks.length} shocks`:`±${lens.shocks[0].bump} ${lens.shocks[0].unit}`;};
    const values=rows.map((row,i)=>shown?[row.reference,row.market,row.quantity??'',lensWords(i),single(i)?.up??'',single(i)?.down??'',single(i)?.delta??'',...details.map(([key])=>fields[i][key]??'')]
      :[row.reference,row.market,row.quantity??'',row.price??'',row.mark??'',row.value??'','',...details.map(([key])=>fields[i][key]??'')]);
    const headers=new Map(groups?.headers.map(h=>[h.key,h])??[]);
    const formats=held.map(holdingFormats);
    const cells=deskCells(rows,colors,{money:(i,value)=>formats[i].money(value),quantity:(i,value)=>formats[i].quantity(value),price:(i,value)=>formats[i].price(value),mark:(i,value)=>formats[i].mark(value),
      factor:(i,value)=>formats[i].factor(value)},i=>shapeOf(held[i]),tableColumns,lenses&&(i=>lenses[i]));
    // How many of a book's, or an instrument type's, positions the run leaves out, by the book and instrument type the run names for each.
    const leftOut=(key:string)=>shown?shown.excluded.filter(e=>view.rowGroup==='book'?e.bookId===key:view.rowGroup==='instrumentType'?e.instrumentType===key:false).length:0;
    table=workspace.render('desk-positions',scope+JSON.stringify([view.mode,view.rowGroup,view.bookIds,view.portfolioIds,view.query,view.columnFilters,shown?'risk':'values']),columns.map(c=>c[1]),values,numeric,busy,{
      keys:tableColumns,cells,searchText:rows.map((row,i)=>shown?`${row.words} ${lensWords(i)}`:row.words),visual:shown?[]:[curveColumn],giveUp:dockedGiveUp(),rowHeight:46,stretch:true,
      visible:columns.map(([key],i)=>i<head.length||view.columns.includes(key)),
      // The Risk lens keeps the widths of the columns it shares with the view, and is laid out afresh: a saved view never keeps it.
      layout:shown?{order:riskColumns.map(([key])=>key),widths:view.grid.widths,sort:view.grid.sort.filter(s=>riskColumns.some(([key])=>key===s.key))}:view.grid,
      onLayout:layout=>{if(!shown)view.grid=withoutColumns(layout,transientKeys);},onReady:sync,
      group:groups?{keys:groups.keys,order:groups.headers.map(h=>h.key),header:(key,count)=>shown?riskGroupHeader(headers.get(key)!,count,leftOut(key)):groupHeader(headers.get(key)!,count,money)}:undefined,
      initial:{query:view.query,columns:tableColumns.map(key=>view.columnFilters[key]??null)},retainMissingFilters:true,
      onRowOpen:index=>void deskPanel.open(index)});
    workspace.columns(tradeColumns,[...new Set([...deskFields,...view.columns])],selected=>{
      if(busy())return;captureLayout();view.columns=selected;render();
    },[...deskFields]);
    renderDeskCharts(get('desk-chart-pv'),plan.pvPanels,'No swaps in the selected books');
    // After a run, the long and short chart becomes the change if each contract moves up; it leaves out what the run leaves out (D218).
    text('desk-chart-position-title',shown?'Change if each contract moves up':'Long and short by market');
    if(shown){
      const change=changeChart(rows.map((row,i)=>({key:positionKey(held[i]),reference:row.reference,market:row.market,commodity:row.commodity})),shown.cells,colors,(i,value)=>formats[i].money(value));
      const excluded=valuationNote(shown.excluded.length);
      renderDeskCharts(get('desk-chart-position'),change.panels,'No position moves under this run',[...(excluded?[excluded]:[]),...(change.unmoved.length?[`Not moved by any shock: ${change.unmoved.join(', ')}`]:[])]);
    }else renderDeskCharts(get('desk-chart-position'),plan.positionPanels,'No positions in the selected books');
    drawn={rows,colors};
  }
  // The curve shape a position's market has at this read: its shape, why it has none, or nothing yet while the shapes are read.
  function shapeOf(h:Holding):CurveShape|string|undefined{
    if(!shapes)return undefined;
    if('unavailable' in shapes)return shapes.unavailable;
    if(h.definitionRevisionId!==shapes.revision)return otherRevision;
    if(projectsFromSeveralCurves(h))return severalCurvesShape;
    return shapes.curves.get(h.curve)??'Curve shape unavailable';
  }
  // The position at a row of the read on screen, as the side panel draws it; nothing while the desk draws no positions.
  function positionInput(index:number):PositionInput|undefined{
    const h=holdings?.holdings[index],row=drawn?.rows[index];
    return h&&row&&drawn?{holding:h,row,formats:holdingFormats(h),shape:shapeOf(h),color:drawn.colors.get(row.commodity)!,risk:positionRisk(h,row)}:undefined;
  }
  function renderPivot(scope:string){
    const read=holdings!;drawn=undefined;
    // A pivot has no desk charts: free those the trade rows drew.
    clearDeskCharts(get('desk-chart-pv'));clearDeskCharts(get('desk-chart-position'));
    const pivotKeys=pivotColumns(view.groupBy,view.measures);
    const columns=blotterColumns.filter(([key])=>pivotKeys.includes(key));
    tableColumns=columns.map(c=>c[0]);
    // Every pivot subtotal says how many positions it leaves out whenever any group leaves one out (D218).
    const counted=read.groups.some(g=>excludedPositions(g.unvaluedPositions)>0);
    const rows:Record<string,string|null>[]=read.groups.map(g=>({...Object.fromEntries(columns.map(([key])=>[key,groupLabel(g,key)])),unvaluedPositions:String(excludedPositions(g.unvaluedPositions))}));
    const required=['instrumentType','unit','currency','priceUnit'];
    const visible=columns.map(([key])=>view.columns.includes(key)||required.includes(key));
    if(!visible.some(Boolean))visible[0]=true;
    const numeric=tableColumns.flatMap((key,i)=>numericFields.includes(key)||isPriceMeasure(key)?[i]:[]);
    const matrix=view.splitBy.length?pivotMatrix(read.groups,view.groupBy,view.splitBy,counted?[...view.measures,'unvaluedPositions']:view.measures):undefined;
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
    table=workspace.render('desk-positions',scope+JSON.stringify([view.mode,view.groupBy,view.measures,view.bookIds,view.portfolioIds,view.query,view.columnFilters]),gridColumns.map(c=>c.title),rawRows.map(row=>gridColumns.map(c=>row[c.key]??'')),gridColumns.flatMap((c,i)=>c.numeric?[i]:[]),busy,{displayRows,keys:tableColumns,columnGroups:matrix?Object.fromEntries(matrix.columns.filter(c=>c.groupKey).map(c=>[c.key,{key:c.groupKey!,title:c.groupTitle!,measureTitle:c.measureTitle!}])):undefined,layout:view.grid,onLayout:layout=>{view.grid=withoutColumns(layout,transientKeys);},onReady:sync,visible:gridVisible,initial:{query:view.query,columns:tableColumns.map(key=>view.columnFilters[key]??null)},retainMissingFilters:true});
    workspace.columns(columns,columns.filter((_,i)=>visible[i]).map(c=>c[0]),selected=>{
      if(busy())return;
      if(!selected.length){render();return;}captureLayout();view.columns=selected;render();
    },required);
    get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(required.includes(n.value)){n.checked=true;n.disabled=true;}});
  }

  // A cleared source cannot receive the old result: where the person is then is nowhere, which no read was made for.
  function requireCurrentRead(started:number,scope:string){
    if(started!==generation||whereAmI()!==scope)throw Error('The account or market selection changed. Reload the Blotter before continuing.');
  }
  async function load(bookingCatalog:BookingCatalog='directory',live=false){
    restore();if(!viewsLoaded)await loadViews();view=parseBlotterViews(JSON.stringify([readView()]))[0];const source=options.source();
    if(source.context&&!source.context.contextId)throw Error('Select a retained historical context before reading holdings.');
    snapshot=source.snapshotId;
    const inputs=JSON.stringify([source.snapshotId,source.context?.contextId??null,view.bookIds,view.portfolioIds]);
    if(live)generation++;else replaceRead(inputs===readInputs?'read':'inputs');
    const started=generation,scope=JSON.stringify([options.profile(),source]);
    const request:ReadRequest={snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})};
    try{
      const selectedView={mode:view.mode,bookIds:view.bookIds,portfolioIds:view.portfolioIds,groupBy:view.mode==='pivot'?view.groupBy:[],measures:view.mode==='pivot'?view.measures:[],pivotConvention:view.mode==='pivot'?'trading_blotter_totals_v1':null,priceNumericalPolicy:view.mode==='pivot'&&view.measures.some(m=>isPriceMeasure(m)&&m.endsWith('Mean'))?priceMeanPolicy:null};
      const summaryViews=deskSummaryViews(view.bookIds,view.portfolioIds);
      const combined=await api<{catalog:Catalog;blotter:Holdings;summaries:Summary[]}>('trading/workspace',{context:{...request,convention:'trading_workspace_v1'},view:selectedView,bookingCatalog,summaries:summaryViews});
      requireCurrentRead(started,scope);
      const selected=combined.catalog.context,reported=combined.blotter.context;
      if(combined.catalog.kind!==bookingCatalog||combined.catalog.kind==='directory'&&Object.hasOwn(combined.catalog,'contracts')||combined.catalog.kind==='priced_contracts'&&!Array.isArray(combined.catalog.contracts))throw Error('The trading workspace returned an inconsistent booking catalog. Refresh and try again.');
      if(!selected.contextId||selected.contextId!==reported.contextId||selected.effectiveOn!==reported.effectiveOn||selected.knownAt!==reported.knownAt||combined.catalog.asOf!==combined.blotter.asOf)throw Error('The trading workspace returned inconsistent market contexts. Refresh and try again.');
      if(!Array.isArray(combined.summaries)||combined.summaries.length!==summaryViews.length)throw Error('The trading workspace did not return the desk summaries. Refresh and try again.');
      // A read the desk refuses leaves the desk it had, and the read that desk was drawn from, as they were.
      const previous={catalog,holdings,summaries,shapes,lastReadRequest};
      catalog=combined.catalog;holdings=combined.blotter;summaries=combined.summaries;shapes=undefined;drafts.reset();
      const reportRequest={context:{...request,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:selected.contextId},view:selectedView};lastReadRequest=reportRequest;
      try{viewControls();render();}catch(error){({catalog,holdings,summaries,shapes,lastReadRequest}=previous);throw error;}
      readInputs=inputs;
      shapesRead=view.mode==='trades'?loadShapes(started,scope,source):Promise.resolve();
      await loadChart(reportRequest,started,scope);requireCurrentRead(started,scope);workspace.closeMenus();
    }catch(error){
      if(started===generation){
        if(!live||(error as {code?:string}).code==='access_denied')clear();
        // A refresh that fails leaves the desk as it was, but it abandoned the read of the curves, if it was under way.
        else if(shapes===undefined)noShapes('the refresh that interrupted their read failed');
      }
      throw error;
    }
  }
  // Every row's curve unavailable, with the reason.
  function noShapes(why:string){shapes={unavailable:`Curve shape unavailable · ${why}`};redrawCurves();deskPanel.sync();}
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
    shapes={revision,curves:found};redrawCurves();deskPanel.sync();
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
    chartState={panels:pivotChartPanels(report.groups,dimensions,splitBy,view.chart.measure),request:chartRequest};renderChartPanels();
  }
  function renderChartPanels(){
    if(!chartState)return;const {panels,request}=chartState,host=get('blotter-chart');
    host.querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(host);host.classList.remove('chart-empty');get('blotter-chart-card').hidden=false;
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
    if(command.path.startsWith('swaps/settlement/'))return;
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
    if(command.path.startsWith('swaps/settlement/')){await settled(command);return;}
    await load();
    await follow(amended,receipt);await followBooked(command,receipt);
  }
  // A settlement command that went through changes what its swap is valued at: risk results run before it no longer hold, and are
  // cleared, with a note when there were any, and an open risk report is read again at the new inputs, keeping a setup's choices (D219).
  // The Blotter is read again as a live correction reads it, so that the panel, and the settlement view the command was sent from, stay,
  // and that view clears what the command consumed and reads its state again: also when the Blotter cannot be read, as a form still
  // holding a command that went through would offer it a second time.
  async function settled(command:RequestCommand){
    const plan=riskRefreshPlan({open:panel.current()?.kind==='risk',edited:risk.edited(),run:risk.shown()||risk.running(),retained:risk.unconfirmed()},'settlement');
    inputsChanged(plan);
    try{await load('directory',true);}finally{await swapEditor.reloadSettlement(command.operationId);}
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
    const found=bookedPosition(holdings.holdings,receipt,command.path.startsWith('swaps/')?'financial_swap':'future');
    if(found.index!==null)await deskPanel.open(found.index);
    if(found.note)options.notice(found.note);
  }
  // A retained settlement command is retried or checked with the settlement view it came from left open, as its outcome reads the
  // Blotter again as a live correction does. Any other retained request is settled by a new read, which closes the panel, so the
  // panel asks about its edits first.
  function retainedAction(work:()=>Promise<void>){if(pending.command?.path.startsWith('swaps/settlement/')){if(!busy())void act(work);}else guarded(work);}
  get('futures-retry').onclick=()=>retainedAction(async()=>{if(!pending.command)throw Error('No retained booking to retry.');await send(pending.command);});
  get('futures-reconcile').onclick=()=>retainedAction(async()=>{
    if(!pending.command)throw Error('No retained booking to reconcile.');
    try{
      const result=await api<Receipt>(pending.command.path.startsWith('swaps/settlement/')?'swaps/settlement/status':pending.command.path.startsWith('swaps/')?'swaps/status':'futures/status',{operationId:pending.command.operationId});
      if(!result.tradeId)throw Error('No committed outcome is visible yet. Retry the original request with the same identity.');
      const command=pending.command,amended=amendedPosition(command);
      pending.clear();uncertain=false;store();
      if(command.path.startsWith('swaps/settlement/')){await settled(command);return;}
      await load();
      await follow(amended,result);await followBooked(command,result);
    }catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}
  });
  get('futures-new').onclick=()=>void openNewTrade();
  field('blotter-mode').onchange=()=>{if(busy())return;get('blotter-pivot-fields').hidden=field('blotter-mode').value!=='pivot';};
  // Grouping arranges the rows already read; every grouping's subtotals came with them. A grouping the read cannot show is
  // refused in the banner, and the grouping on screen stays.
  field('blotter-row-group').onchange=()=>void act(async()=>{
    const shown=view.rowGroup;captureLayout();view.rowGroup=(field('blotter-row-group').value||null) as RowGroup;
    try{render();}catch(error){view.rowGroup=shown;field('blotter-row-group').value=shown??'';throw error;}
  });
  get('blotter-apply').onclick=()=>guarded(load);
  get('blotter-save').onclick=()=>guarded(async()=>{
    const draft=readView();const next=parseBlotterViews(JSON.stringify([{...draft,name:field('blotter-name').value}]))[0];
    const saved=await layoutApi<{version:string;layout:BlotterView}>('table-views/blotter',{layout:next,expectedVersion:draftBase?.name===next.name?draftBase.version:versions.get(next.name)??null});
    versions.set(next.name,saved.version);draftBase={name:next.name,version:saved.version};savedViews=[...savedViews.filter(v=>v.name!==next.name),saved.layout];earlierViews=earlierViews.filter(name=>name!==next.name);
    view=structuredClone(saved.layout);viewControls();field('blotter-saved').value=next.name;text('blotter-view-note','Saved to your account. Dates and market inputs are selected separately.');table=undefined;workspace.closeMenus();await load();
  });
  // An earlier-format view is named so it can be saved again under its name, and explained as a notice, not an error.
  field('blotter-saved').onchange=()=>guarded(async()=>{const name=field('blotter-saved').value;if(earlierViews.includes(name)){field('blotter-name').value=name;options.notice(earlierFormatNote(name));return;}const saved=savedViews.find(v=>v.name===name);if(!saved)return;draftBase={name:saved.name,version:versions.get(saved.name)!};table=undefined;view=structuredClone(saved);field('blotter-name').value=view.name;viewControls();workspace.closeMenus();await load();},()=>{field('blotter-saved').value=draftBase?.name??'';});
  get('blotter-views-reload').onclick=()=>guarded(async()=>{
    const name=field('blotter-saved').value;
    await loadViews();
    const saved=savedViews.find(v=>v.name===name);
    if(saved){draftBase={name:saved.name,version:versions.get(saved.name)!};table=undefined;view=structuredClone(saved);field('blotter-name').value=view.name;viewControls();await load();}
    text('blotter-view-note',saved?'The selected account layout has been reloaded.':'Saved-view list refreshed. Choose a view to restore it.');
  });
  get('blotter-delete').onclick=()=>void act(async()=>{const name=field('blotter-saved').value;if(!name)throw Error('Choose a saved view to delete.');await layoutApi('table-views/blotter/delete',{name,expectedVersion:draftBase?.name===name?draftBase.version:versions.get(name)});if(draftBase?.name===name)draftBase=undefined;savedViews=savedViews.filter(v=>v.name!==name);earlierViews=earlierViews.filter(n=>n!==name);versions.delete(name);viewControls();field('blotter-name').value='';text('blotter-view-note','Saved view deleted. The displayed data is unchanged.');});
  // A trade-row export states the trade fields of the rows the filters show, the desk's own and the added columns, exactly.
  function tradeExport(grid:ReturnType<typeof renderDataGrid>):{headers:string[];rows:string[][];numeric:number[]}{
    const read=holdings!,columns=tradeColumns.filter(([key])=>(deskFields as readonly string[]).includes(key)||key==='currency'||view.columns.includes(key));
    const rows=grid.shownRows().map(i=>{const h=read.holdings[i],fields=tradeFields(h);return columns.map(([key])=>notValued(h,key)??noted(drawn?.rows[i],key)??fields[key]??'');});
    return {headers:columns.map(c=>c[1]),rows,numeric:columns.flatMap(([key],i)=>numericFields.includes(key)?[i]:[])};
  }
  get('blotter-export').onclick=()=>{if(busy()||!table||!holdings||!lastReadRequest)return;const pivot=view.mode==='pivot';const metadata=[['Blotter snapshot'],['As of',holdings.asOf],['Knowledge cutoff',holdings.context.knownAt],['Convention',holdings.convention],['Display',view.mode],['Grouping',pivot?view.groupBy.join(' / '):rowGroupLabel(view.rowGroup)],['Measures',pivot?view.measures.join(' / '):'Trade rows'],['Portfolios',view.portfolioIds.length?catalog!.books.filter(b=>view.portfolioIds.includes(b.portfolio_id)).map(b=>b.portfolio).filter((v,i,a)=>a.indexOf(v)===i).join(' / '):'All authorized portfolios'],['Books',view.bookIds.length?catalog!.books.filter(b=>view.bookIds.includes(b.id)).map(b=>b.name).join(' / '):'All books in selected portfolios'],['Search',table.state().query],['Column filters',JSON.stringify(Object.fromEntries(table.state().columns.flatMap((v,i)=>v===null?[]:[[tableColumns[i],v]])))],['Numerical policy',holdings.pivotNumericalPolicy],['Pivot convention',pivot?'trading_blotter_totals_v1':'Not applicable'],['Positions not valued at this close',String(excludedPositions(holdings.unvaluedPositions))],[]];
    const shown=pivot?{headers:table.headers(),rows:table.visibleRows(),numeric:table.numeric()}:tradeExport(table);
    const csv=guardedCsv(metadata)+guardedCsv([shown.headers,...shown.rows],shown.numeric);void act(async()=>{try{await api('trading/blotter',lastReadRequest);}catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}downloadCsv('leanetrm-blotter.csv',csv);});
  };
  get('futures-refresh').onclick=()=>guarded(load);
  // A Blotter view a navigation target names, applied as Apply applies the view's controls.
  async function show(selection:BlotterSelection){
    restore();if(!viewsLoaded)await loadViews();
    const next:BlotterView={...structuredClone(view),mode:selection.mode,bookIds:[...selection.bookIds],portfolioIds:[...selection.portfolioIds],
      groupBy:[...selection.groupBy],splitBy:[],measures:[...selection.measures]};
    if(view.mode!==next.mode||JSON.stringify([view.groupBy,view.splitBy,view.measures])!==JSON.stringify([next.groupBy,next.splitBy,next.measures]))relayout(next);
    view=next;viewControls();await load();
  }
  // The trade the panel shows, as the page reports it: its position, at the revision the Blotter holds, or at the one its form shows
  // while its form is open, which can be an earlier one.
  function shownTrade():TradeShown|null{
    const h=deskPanel.shown();
    if(!h)return null;
    const form=editor.displayedRevision()??swapEditor.displayedRevision();
    return {tradeId:h.tradeId,instrumentType:h.instrumentType==='financial_swap'?'financial_swap':'future',revision:form??Number(h.revision),reference:h.tradeReference};
  }
  // The new trade the panel holds, as the page reports it: of its product, or of none until the person has chosen one.
  function newTradeShown():EntryShown|null{const mode=panel.current();return mode?.kind==='new'?{product:mode.product}:null;}
  return {load,reset,sync,show,
    // A live correction reads the desk again, a position or new trade in the panel with it unless the person has edits in it, and the risk
    // report at the corrected inputs, carrying a setup's choices over; it clears a risk run with a note. No live view can book, so no form of
    // a new trade is held while corrections arrive.
    async liveRefresh(){
      const plan=liveRefreshPlan({panel:panel.current(),unsaved:editing(),riskRun:risk.shown()||risk.running(),riskRetained:risk.unconfirmed()});
      inputsChanged(plan.risk);
      await load(catalog?.kind??'directory',true);
      if(plan.risk.reopen)await risk.open();
      if(plan.reloadPanel){await position.reload();await swapEditor.liveRefresh();}
    },
    refresh(){risk.refresh();booking.refresh();editor.refresh();swapEditor.refresh();captureFilters();render();},
    // Opens a trade a target names, as the position it is in at the revision the Blotter holds. An earlier revision is said not to be shown.
    async openTrade(tradeId:string,instrumentType:'future'|'financial_swap',revision:number|null){
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
    report:()=>({risk:panel.current()?.kind==='risk',trade:shownTrade(),entry:newTradeShown(),lens:lensShown()?'risk' as const:'values' as const,
      view:{mode:view.mode,bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds],groupBy:[...view.groupBy],measures:[...view.measures]}}),
    // A form of the panel with edits not yet sent, the risk report's setup among them, or a trade request or risk job whose outcome is unconfirmed.
    unsaved:()=>!!pending.command||editing()||risk.unconfirmed(),
    // Whether the panel holds the form of an amendment or of a new trade, or a risk report's setup, with edits, and the question asked before it is given up: false means the person kept it. The forms
    // are dropped without a question when the page is left on the person's word, as the assistant's Go anyway is.
    editing,leave,dropForms};
}
