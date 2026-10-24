import {draftValuation} from './draft-valuation.js';
import {amountText} from './amount-display.js';
import {tradeAnnotations,type PartyCatalog,type Trader} from './trade-annotations.js';
import {setupSwapEditor,readSwapView} from './swap-editor.js';
import {setupTradeEditor,readTradeView} from './trade-editor.js';
import {futuresContractDescription,futuresContractKey,type FuturesContract as Contract} from './futures-contract.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {knownTradeRejection,retainedText} from './trade-requests.js';
import {setupRisk,riskDenied} from './risk-view.js';
import {futuresInstrumentEditor} from './futures-instrument-editor.js';
import {createTableWorkspace} from './table-workspace.js';
import {renderDataGrid} from './data-grid.js';
import {pivotMatrix,pivotChartPanels,groupLabel,type PivotGroup} from './analytics-model.js';
import {renderChart,clearChart,type ChartSpec} from './chart-view.js';
import {trackEdits} from './edit-tracking.js';
import type {BlotterSelection,TradeShown} from './assistant-pages.js';
import {isPriceMeasure,priceMeanPolicy,blotterColumns,deskFields,groupChoices,measureChoices,newBlotterView,parseBlotterViews,pivotColumns,readSavedViews,rowGroups,withoutColumns,type BlotterView,type RowGroup} from './blotter-layout.js';
import {guardedCsv,downloadCsv} from './csv-export.js';
import {friendly,publicText,displayInstant,deliveryLabel,curveName} from './market-view.js';
import {valuationReason,valuationNote,excludedPositions,valuationMeasure} from './valuation-status.js';
import {commodityColors,deskGroups,deskSummaryViews,holdingFormats,positionChartPanels,positionRows,positionsTile,priceUnit,pvChartPanels,summaryTiles,valueFields,type CurveShape,type DeskRow,type Holding,type Summary} from './desk-model.js';
import {askLeave,liveRefreshPlan,panelPlacement,sameMode,setupSidePanel,type PanelMode} from './side-panel.js';
import {setupPositionPanel,type PositionInput} from './position-panel.js';
import {followedPosition,requireTradeRows,revisionNote,setupDeskPanel,tradeIndex} from './desk-panel.js';
import {clearDeskCharts,deskCells,deskColumns,groupHeader,renderDeskCharts,renderMoneyTile,renderPositionsTile,renderRiskTile} from './desk-view.js';

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
  // What the page reports of itself has changed: the panel moved to another mode, or its form was closed.
  reported:()=>void;
  // The panel's form started or stopped holding unsaved edits.
  unsavedChanged:()=>void}){
  const {api,act,busy}=options;
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
  const workspace=createTableWorkspace(get('blotter-workspace'),{
    key:'blotter',rowMode:'trades',rowLabel:'Individual trades',dimensions:groupChoices,measures:measureChoices,
    pivotHelp:'Totals keep instrument types, quantity specifications and currencies separate. Blank measures are unavailable. Lot totals require dated-contract grouping. Price measures keep quote multipliers separate; averages use 18 decimal places, ties to even. Changing display mode clears row filters.',
    scopes:[['portfolios','Portfolios'],['books','Books']],actions:[['risk','Shocks'],['refresh','Refresh'],['export','Export'],['new','New trade']],onApply:()=>guarded(load),
    lead:get('desk'),overview:get('desk-charts'),aside:get('side-panel'),rowGroups:rowGroups.map(group=>[group,rowGroupLabels[group]] as const),
  });
  // Existing booking controls share the table toolbar.
  get('blotter-refresh').id='futures-refresh';get('blotter-new').id='futures-new';get('blotter-rows').id='desk-positions';
  let catalog:Catalog|undefined,holdings:Holdings|undefined,summaries:Summary[]|undefined,owner='',snapshot='',selectedContract='',uncertain=false,generation=0;
  // The curve shapes of the current read, once read: by curve, a shape or why it is unavailable; or why none could be read.
  let shapes:{revision:string;curves:Map<string,CurveShape|string>}|{unavailable:string}|undefined;
  const pricedCatalog=()=>catalog?.kind==='priced_contracts'?catalog:undefined;
  const pending=new PendingCommand();
  const annotations=tradeAnnotations('futures',options.zone);
  const instrument=futuresInstrumentEditor(get('futures-instrument'),{contract:'futures-contract',lots:'futures-lots',price:'futures-execution',terms:'futures-contract-terms'},{edited:()=>bookingEdits.mark()});
  const draft=draftValuation(get('futures-draft-valuation'),{product:'futures',api,act,busy,denied:clear,key:()=>JSON.stringify(readContext()),draft:async()=>{
    const c=pricedCatalog()?.contracts.find(c=>contractKey(c)===field('futures-contract').value);if(!c)throw Error('Choose an exact dated futures contract.');
    return{context:readContext(),curve:c.curve,contractId:c.id,lots:field('futures-lots').value,executionPrice:field('futures-execution').value};
  }});
  get('futures-form').addEventListener('input',draft.reset);
  const bookingEdits=trackEdits(get('futures-form'));
  let lastReadRequest:unknown;
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
  const risk=setupRisk({api,act,busy,zone:options.zone,profile:options.profile,canRun:()=>options.canRunRisk()&&!!holdings,onDenied:clear,onChange:renderRisk,scope:()=>{
    if(!lastReadRequest)throw Error('Load the blotter before running risk.');
    return {context:{...(lastReadRequest as {context:Record<string,unknown>}).context},bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds]};
  }});
  get('blotter-risk').onclick=()=>void act(risk.open);
  function readContext(){const source=options.source();return {snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})};}
  const editor=setupTradeEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),leave:()=>panel.leave(),unsavedChanged:options.unsavedChanged,
    queue:async command=>{if(pending.command)throw Error('Reconcile the retained request before submitting another command.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});

  const swapEditor=setupSwapEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,host:()=>panel.element('form'),leave:()=>panel.leave(),unsavedChanged:options.unsavedChanged,
    queue:async command=>{if(pending.command)throw Error('Reconcile the retained request first.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});
  // The side panel beside the positions: a position's curve, terms, risk and history, and the form of its amendment. Opening a row
  // opens it; Amend puts the amendment's form in the Terms tab, and a new swap has the whole panel.
  const main=document.querySelector('main')!,phone=window.matchMedia('(max-width:760px)');
  const editing=()=>editor.unsaved()||swapEditor.unsaved();
  const docked=()=>panelPlacement(main.clientWidth,document.body.classList.contains('assistant-open'))==='docked';
  // A docked panel takes the grid's width, so the grid gives up the columns the panel shows for its position, Curve first and then
  // Mark, for as long as its columns do not fit the room it has left.
  const dockedGiveUp=()=>panel.current()!==null&&docked()?[curveColumn,markColumn]:[];
  const panel=setupSidePanel(get('side-panel'),{
    // What the panel holds that switching would lose: an amendment of its position in progress.
    unsaved:editing,confirmLeave:askLeave(get('side-panel')),docked,
    // On a phone the panel is a sheet over the page, which stays out of reach while it is open, unless the Blotter has been left.
    sheet:()=>phone.matches&&!get('futures-page').hidden,
    changed:()=>{table?.fitColumns(dockedGiveUp());modeChanged();placeRetained();},closed:()=>deskPanel.closed()});
  // A request whose outcome is not known is kept with its Check status and Retry in a card, which is in the panel while the panel is open,
  // whatever it shows, and on the page when it is not, so that nothing leaves the person with no way to find out what became of it.
  const retained=get('futures-pending');
  function placeRetained(){const slot=get(panel.current()!==null?'side-panel-retained':'futures-retained');if(retained.parentElement!==slot)slot.append(retained);}
  // The forms belong to the panel's mode: when it moves to another, whatever they held is dropped (the person was asked first if they
  // held edits), and the page says what it now reports.
  let formsFor:PanelMode=null;
  function modeChanged(){
    const mode=panel.current();
    if(sameMode(mode,formsFor))return;
    formsFor=mode;editor.reset();swapEditor.reset();
    if(mode!==null&&mode.kind!=='position')deskPanel.left();
    options.reported();
  }
  // The panel docks or overlays by the width the main area has: it is placed again when that changes, not when only its height does.
  let mainWidth=main.clientWidth;
  new ResizeObserver(()=>{if(main.clientWidth!==mainWidth){mainWidth=main.clientWidth;panel.place();}}).observe(main);
  new MutationObserver(()=>panel.place()).observe(document.body,{attributes:true,attributeFilter:['class']});
  const position=setupPositionPanel(panel,{zone:options.zone,busy,canAmend:()=>!!catalog?.canAmendTrades,curveListed:options.curveListed,context:()=>JSON.stringify(readContext()),onDenied:clear,
    read:(h,formats)=>h.instrumentType==='financial_swap'?readSwapView(api,readContext(),h,formats,busy):readTradeView(api,readContext(),h,formats),
    amendment:{
      open:h=>void act(()=>h.instrumentType==='financial_swap'?swapEditor.open(h,Number(h.revision)):editor.open(h,Number(h.revision))),
      // A future's form is open for its binding, a swap's for its instrument.
      shown:h=>{const future=editor.shown(),swap=swapEditor.shown();return !!future&&future.tradeId===h.tradeId&&future.bindingId===h.bindingId||!!swap&&swap.tradeId===h.tradeId&&swap.contractId===h.contractId;},
      close:async()=>{await leave();},
    },
    openCurve:(curve,contractId)=>guarded(()=>options.openCurve(curve,contractId))});
  const deskPanel=setupDeskPanel({shell:panel,position,holdings:()=>holdings?.holdings,input:positionInput,grid:()=>table,notice:options.notice});
  const kindDialog=get<HTMLDialogElement>('trade-kind');
  get('trade-kind-close').onclick=()=>kindDialog.close();
  get('trade-kind-ppa').onclick=()=>{kindDialog.close();guarded(options.newPpa);};
  get('trade-kind-future').onclick=()=>{kindDialog.close();guarded(async()=>{await load('priced_contracts');get('futures-booking').hidden=false;bookingEdits.clear();field('futures-book').focus();});};
  get('trade-kind-swap').onclick=()=>{kindDialog.close();get('futures-booking').hidden=true;void openNewSwap();};
  // A new swap is booked in the panel, which it takes whole. What the panel held, and a booking already begun, are left only when
  // the person says so, which is asked before the page is busy, not while it is.
  const newSwap:PanelMode={kind:'new',product:'swaps'};
  async function openNewSwap(){
    if(busy())return;
    if(sameMode(panel.current(),newSwap)&&!await panel.show(null))return;
    if(!await panel.show(newSwap))return;
    const title=document.createElement('h2');title.className='side-panel-title';title.textContent='New swap';panel.element('head').replaceChildren(title);
    await act(async()=>{
      try{await swapEditor.openNew();}catch(error){panel.reset();throw error;}
      if(sameMode(panel.current(),newSwap))panel.form(true);
    });
  }
  // Gives the panel's forms up without asking, for a page that is left on the person's word, so that no form stays held that they cannot return to.
  function dropForms(){editor.reset();swapEditor.reset();options.reported();}
  // Asks whether to give up what the panel holds unsaved, when it holds any, and closes its form once the person agrees: false means they
  // kept it. The panel asks for a switch of its own mode; this is the question every other way out of the form asks too.
  async function leave(){if(!await panel.leave())return false;dropForms();return true;}
  // An action of the person's that gives the panel's form up, by reading the Blotter again, which closes the panel, or by leaving it,
  // asks first about the edits the panel holds, before the page is busy: a question asked then would be asked of a page with every
  // control disabled. `kept` hears that the person kept them, for a control that has already changed.
  function guarded(work:()=>Promise<void>,kept?:()=>void){void (async()=>{if(busy())return;if(await leave())await act(work);else kept?.();})();}
  const selected=(id:string)=>Array.from(get<HTMLSelectElement>(id).selectedOptions,o=>o.value);
  function choices(id:string,rows:{id:string;name:string}[],ids:string[]){
    const select=get<HTMLSelectElement>(id);select.replaceChildren(...rows.map(r=>new Option(publicText(r.name),r.id,false,ids.includes(r.id))),...ids.filter(id=>!rows.some(r=>r.id===id)).map(id=>new Option('Unavailable saved selection',id,false,true)));
  }
  function captureFilters(){if(!table)return;const state=table.state();view.query=state.query;view.columnFilters=Object.fromEntries(state.columns.flatMap((value,i)=>value===null||transientKeys.has(tableColumns[i])?[]:[[tableColumns[i],value]]));}
  function captureLayout(){captureFilters();if(table?.ready())view.grid=withoutColumns(table.layout(),transientKeys);}
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
    try{const saved=JSON.parse(sessionStorage.getItem(storageKey())??'null');if(saved&&['futures/book','futures/amend','swaps/book','swaps/amend'].includes(saved.path)&&typeof saved.operationId==='string'&&saved.body&&typeof saved.body==='object'){pending.command=saved;uncertain=true;}}catch{/* An unreadable local draft grants no authority. */}
  }
  // Today's risk report opens below the positions; the tile says whether it is open or has results. The report tells the
  // page whenever that changes, including its own Close and a queued job that finishes on its own timer.
  function renderRisk(){
    if(!holdings)return;
    renderRiskTile(get('desk-tile-risk'),{state:risk.shown()?'results':get('risk-card').hidden?'none':'open',denial:options.canRunRisk()?null:riskDenied,busy:busy()},
      ()=>{if(get('risk-card').hidden)void act(risk.open);else get('risk-card').scrollIntoView({block:'start'});});
  }
  function sync(){instrument.sync(busy()||!pricedCatalog());draft.sync();risk.sync();editor.sync();swapEditor.sync();position.sync();panel.place();get('blotter-risk').hidden=false;get('blotter-risk').textContent='Portfolio shocks';workspace.busy(busy());text('futures-booking-availability',pending.command?'A trade request is retained. Reconcile or retry it before creating another.':catalog?.canBook?'Working data · booking is enabled at the selected As of time.':catalog?.bookingUnavailableReason??options.bookingUnavailableReason());get('futures-new').setAttribute('aria-describedby','futures-booking-availability');get<HTMLFieldSetElement>('futures-fields').disabled=busy()||!pricedCatalog();get('futures-form').hidden=!pricedCatalog();get<HTMLButtonElement>('futures-book-submit').disabled=busy()||!pricedCatalog()?.canBook;retained.hidden=!pending.command;if(pending.command)text('futures-pending-text',retainedText(pending.command));placeRetained();get<HTMLButtonElement>('futures-new').disabled=busy()||!catalog||!!pending.command;get<HTMLButtonElement>('blotter-export').disabled=busy()||!table?.ready();get<HTMLButtonElement>('futures-retry').disabled=busy()||!(pending.command?.path.endsWith('/amend')?catalog?.canAmendTrades:catalog?.canBook);get<HTMLButtonElement>('futures-reconcile').disabled=busy();if(view.mode==='pivot')get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(['instrumentType','unit','currency','priceUnit'].includes(n.value))n.disabled=true;});}
  function clearDesk(){get('desk').hidden=true;get('desk-charts').hidden=true;for(const id of ['desk-tile-swapPv','desk-tile-futuresPnl','desk-tile-positions','desk-tile-risk'])get(id).replaceChildren();clearDeskCharts(get('desk-chart-pv'));clearDeskCharts(get('desk-chart-position'));}
  function clear(){instrument.clear();generation++;chartState=undefined;drawn=undefined;draft.reset();editor.reset();swapEditor.reset();if(kindDialog.open)kindDialog.close();risk.reset();catalog=undefined;holdings=undefined;summaries=undefined;shapes=undefined;table=undefined;deskPanel.reset();lastReadRequest=undefined;choose('futures-contract',[],'');get('blotter-chart').querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(get('blotter-chart'));get('blotter-chart-card').hidden=true;text('header-calculation-time','');get('futures-booking').hidden=true;get('blotter-books').replaceChildren();get('blotter-portfolios').replaceChildren();workspace.clear();clearDesk();text('futures-summary','Choose a market date to inspect holdings.');text('futures-terms','Select a dated contract to see its terms.');sync();}
  function reset(){selectedContract='';clear();restore();forgetViews();viewControls();snapshot='';get<HTMLFormElement>('futures-form').reset();get('futures-pending').hidden=true;}
  function choose(id:string,rows:{id:string;name:string}[],selected:string,placeholder='Choose…'){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option(placeholder,''),...rows.map(r=>new Option(publicText(r.name),r.id)));select.value=rows.some(r=>r.id===selected)?selected:'';}
  const contractKey=futuresContractKey;
  function terms(){const c=pricedCatalog()?.contracts.find(c=>contractKey(c)===field('futures-contract').value);text('futures-terms',c?futuresContractDescription(c,options.zone()):'Select a dated contract to see its terms.');instrument.render(c,options.zone(),(value,tick)=>amountText(value,'price',tick));}
  // A trade row's fields as the trade columns and exports state them: exact amounts, readable names.
  function tradeFields(h:Holding):Record<string,string|null>{
    const {booking,amountFormat,fixedAmountFormat,commodity,quantitySpecification,currencyReference,...fields}=h;
    return {...fields,priceUnit:priceUnit(h),tradeDate:booking.tradeDate,legalEntity:booking.legalEntity??null,counterparty:booking.counterparty??null,instrumentType:friendly(h.instrumentType),commodity:commodity.name,market:curveName(h.curve),contract:deliveryLabel(h.deliveryFrom,h.deliveryTo),executed:displayInstant(h.executedAt,options.zone()),settlement:publicText(h.settlement==='unprocessed'?'Unprocessed':h.settlement)};
  }
  // What a trade row's value cell says when the close cannot value it, on screen and in exports (D214).
  function notValued(h:Holding,key:string){return h.unavailableReason&&valueFields(h.instrumentType).includes(key)?`Not valued · ${valuationReason(h.unavailableReason,h.instrumentType)}`:undefined;}
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
    annotations.populate(catalog.bookingActor,catalog,annotations.read());
    choose('futures-book',catalog.books.map(b=>({id:b.id,name:`${b.portfolio} · ${b.name}`})),field('futures-book').value);
    instrument.populate(pricedCatalog()?.contracts??[],selectedContract);terms();
    if(pricedCatalog())selectedContract=field('futures-contract').value;
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
    const held=holdings!.holdings,{rows,fields,groups}=plan;
    // Every other trade column is present, shown when the view chooses it, so a filter on a hidden one stays active.
    const details=tradeColumns.filter(([key])=>!(deskFields as readonly string[]).includes(key));
    const columns=[...deskColumns,...details];
    tableColumns=columns.map(c=>c[0]);transientKeys=new Set(['curve']);
    const numeric=tableColumns.flatMap((key,i)=>i>=deskColumns.length?(numericFields.includes(key)?[i]:[]):['quantity','executionPrice','mark','presentValue'].includes(key)?[i]:[]);
    const values=rows.map((row,i)=>[row.reference,row.market,row.quantity,row.price??'',row.mark??'',row.value??'','',...details.map(([key])=>fields[i][key]??'')]);
    const headers=new Map(groups?.headers.map(h=>[h.key,h])??[]);
    const formats=held.map(holdingFormats);
    const cells=deskCells(rows,colors,{money:(i,value)=>formats[i].money(value),quantity:(i,value)=>formats[i].quantity(value),price:(i,value)=>formats[i].price(value),mark:(i,value)=>formats[i].mark(value)},i=>shapeOf(held[i]));
    table=workspace.render('desk-positions',scope+JSON.stringify([view.mode,view.rowGroup,view.bookIds,view.portfolioIds,view.query,view.columnFilters]),columns.map(c=>c[1]),values,numeric,busy,{
      keys:tableColumns,cells,searchText:rows.map(row=>row.words),visual:[curveColumn],giveUp:dockedGiveUp(),rowHeight:46,stretch:true,visible:columns.map(([key],i)=>i<deskColumns.length||view.columns.includes(key)),layout:view.grid,onLayout:layout=>{view.grid=withoutColumns(layout,transientKeys);},onReady:sync,
      group:groups?{keys:groups.keys,order:groups.headers.map(h=>h.key),header:(key,count)=>groupHeader(headers.get(key)!,count,money)}:undefined,
      initial:{query:view.query,columns:tableColumns.map(key=>view.columnFilters[key]??null)},retainMissingFilters:true,
      onRowOpen:index=>void deskPanel.open(index)});
    workspace.columns(tradeColumns,[...new Set([...deskFields,...view.columns])],selected=>{
      if(busy())return;captureLayout();view.columns=selected;render();
    },[...deskFields]);
    renderDeskCharts(get('desk-chart-pv'),plan.pvPanels,'No swaps in the selected books');
    renderDeskCharts(get('desk-chart-position'),plan.positionPanels,'No positions in the selected books');
    drawn={rows,colors};
  }
  // The curve shape a position's market has at this read: its shape, why it has none, or nothing yet while the shapes are read.
  function shapeOf(h:Holding):CurveShape|string|undefined{
    if(!shapes)return undefined;
    if('unavailable' in shapes)return shapes.unavailable;
    if(h.definitionRevisionId!==shapes.revision)return 'Curve shape unavailable · this market is defined in another revision than the curves of this close';
    return shapes.curves.get(h.curve)??'Curve shape unavailable';
  }
  // The position at a row of the read on screen, as the side panel draws it; nothing while the desk draws no positions.
  function positionInput(index:number):PositionInput|undefined{
    const h=holdings?.holdings[index],row=drawn?.rows[index];
    return h&&row&&drawn?{holding:h,row,formats:holdingFormats(h),shape:shapeOf(h),color:drawn.colors.get(row.commodity)!}:undefined;
  }
  function renderPivot(scope:string){
    const read=holdings!;drawn=undefined;
    // A pivot has no desk charts: free those the trade rows drew.
    clearDeskCharts(get('desk-chart-pv'));clearDeskCharts(get('desk-chart-position'));
    const pivotKeys=pivotColumns(view.groupBy,view.measures);
    const columns=blotterColumns.filter(([key])=>pivotKeys.includes(key));
    tableColumns=columns.map(c=>c[0]);
    // Every pivot subtotal says how many positions it leaves out whenever any group leaves one out (D214).
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

  function requireCurrentRead(started:number,scope:string){
    let current:string|undefined;
    try{current=JSON.stringify([options.profile(),options.source()]);}catch{/* A cleared source cannot receive the old result. */}
    if(started!==generation||current!==scope)throw Error('The account or market selection changed. Reload the Blotter before continuing.');
  }
  async function load(bookingCatalog:BookingCatalog='directory',live=false){
    restore();if(!viewsLoaded)await loadViews();view=parseBlotterViews(JSON.stringify([readView()]))[0];const source=options.source();
    if(source.context&&!source.context.contextId)throw Error('Select a retained historical context before reading holdings.');
    if(snapshot!==source.snapshotId){if(!live){get<HTMLFormElement>('futures-form').reset();selectedContract='';}snapshot=source.snapshotId;}
    if(live)generation++;else clear();
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
      catalog=combined.catalog;holdings=combined.blotter;summaries=combined.summaries;shapes=undefined;
      const reportRequest={context:{...request,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:selected.contextId},view:selectedView};lastReadRequest=reportRequest;
      try{viewControls();render();}catch(error){({catalog,holdings,summaries,shapes,lastReadRequest}=previous);throw error;}
      if(view.mode==='trades')void loadShapes(started,scope,source);
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
  function noShapes(why:string){shapes={unavailable:`Curve shape unavailable · ${why}`};table?.redrawCells(curveColumn);deskPanel.sync();}
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
      const chunk=curves.slice(i,i+curvesPerShapeRead);
      try{
        const read=await api<{curves:CurveShape[];context?:{contextId?:string}}>('market/curve-shapes',{snapshotId:source.snapshotId,curves:chunk});
        // The read states the context it was made in: the selected historical one, or none for working data.
        if(read.context?.contextId!==source.context?.contextId)for(const curve of chunk)found.set(curve,'Curve shape unavailable · the curves were read in another market context');
        else for(const curve of read.curves)if(chunk.includes(curve.curve))found.set(curve.curve,curve);
      }catch(error){
        // A role that cannot read market data is told so, rather than that the read failed.
        const why=(error as {code?:string}).code==='access_denied'?'this account cannot read market data':'the curve read failed';
        for(const curve of chunk)found.set(curve,`Curve shape unavailable · ${why}`);
      }
      if(!current())return;
    }
    shapes={revision,curves:found};table?.redrawCells(curveColumn);deskPanel.sync();
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
  // The form a request came from is done with it, whatever became of it: what it held was sent, or is kept by the retained request.
  // A new swap's form was the whole panel, which has nothing left to show with it closed.
  function done(command:RequestCommand){
    if(command.path==='futures/amend')editor.reset();
    if(command.path.startsWith('swaps/')){swapEditor.reset();if(panel.current()?.kind==='new')panel.reset();}
  }
  // What the backend says of a trade request it has committed: the trade, and the binding the request left it with.
  type Receipt={tradeId?:string;bindingId?:string};
  async function send(command:RequestCommand){
    const retrying=uncertain;uncertain=true;store();
    let receipt:Receipt|undefined;
    try{receipt=await api<Receipt>(command.path,{...command.body,operationId:command.operationId});}
    catch(error){
      // A rejected retry says nothing about the original uncertain outcome.
      if(!retrying&&knownTradeRejection((error as {code?:string}).code)){pending.clear();uncertain=false;store();}
      done(command);
      if((error as {code?:string}).code==='access_denied')clear();sync();throw error;
    }
    const amended=amendedPosition(command);
    // What was sent is no longer an edit: the read that follows closes the form without asking.
    done(command);
    pending.clear();uncertain=false;store();get('futures-booking').hidden=true;await load();
    await follow(amended,receipt);
  }
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
  get<HTMLFormElement>('futures-form').onsubmit=event=>{event.preventDefault();guarded(async()=>{
    const priced=pricedCatalog();if(!priced?.canBook)throw Error('Return to current inputs with trading authority before booking.');
    const c=priced.contracts.find(c=>contractKey(c)===field('futures-contract').value);if(!c)throw Error('Choose the exact dated contract.');
    const body={...annotations.read(),snapshotId:options.source().snapshotId,convention:options.source().convention,knowledgeMode:'current',bookId:field('futures-book').value,curve:c.curve,contractId:c.id,
      lots:field('futures-lots').value,executionPrice:field('futures-execution').value};
    const command=pending.begin('futures/book',body,undefined,'New futures trade');store();sync();await send(command);
  });};
  get('futures-retry').onclick=()=>guarded(async()=>{if(!pending.command)throw Error('No retained booking to retry.');await send(pending.command);});
  get('futures-reconcile').onclick=()=>guarded(async()=>{
    if(!pending.command)throw Error('No retained booking to reconcile.');
    try{
      const result=await api<Receipt>(pending.command.path.startsWith('swaps/')?'swaps/status':'futures/status',{operationId:pending.command.operationId});
      if(!result.tradeId)throw Error('No committed outcome is visible yet. Retry the original request with the same identity.');
      const amended=amendedPosition(pending.command);
      pending.clear();uncertain=false;store();get('futures-booking').hidden=true;await load();
      await follow(amended,result);
    }catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}
  });
  get('futures-new').onclick=()=>{if(busy()||!catalog)return;kindDialog.showModal();};
  get('futures-cancel').onclick=()=>{if(!busy()){get('futures-booking').hidden=true;bookingEdits.clear();}};
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
    const rows=grid.shownRows().map(i=>{const h=read.holdings[i],fields=tradeFields(h);return columns.map(([key])=>notValued(h,key)??fields[key]??'');});
    return {headers:columns.map(c=>c[1]),rows,numeric:columns.flatMap(([key],i)=>numericFields.includes(key)?[i]:[])};
  }
  get('blotter-export').onclick=()=>{if(busy()||!table||!holdings||!lastReadRequest)return;const pivot=view.mode==='pivot';const metadata=[['Blotter snapshot'],['As of',holdings.asOf],['Knowledge cutoff',holdings.context.knownAt],['Convention',holdings.convention],['Display',view.mode],['Grouping',pivot?view.groupBy.join(' / '):rowGroupLabel(view.rowGroup)],['Measures',pivot?view.measures.join(' / '):'Trade rows'],['Portfolios',view.portfolioIds.length?catalog!.books.filter(b=>view.portfolioIds.includes(b.portfolio_id)).map(b=>b.portfolio).filter((v,i,a)=>a.indexOf(v)===i).join(' / '):'All authorized portfolios'],['Books',view.bookIds.length?catalog!.books.filter(b=>view.bookIds.includes(b.id)).map(b=>b.name).join(' / '):'All books in selected portfolios'],['Search',table.state().query],['Column filters',JSON.stringify(Object.fromEntries(table.state().columns.flatMap((v,i)=>v===null?[]:[[tableColumns[i],v]])))],['Numerical policy',holdings.pivotNumericalPolicy],['Pivot convention',pivot?'trading_blotter_totals_v1':'Not applicable'],['Positions not valued at this close',String(excludedPositions(holdings.unvaluedPositions))],[]];
    const shown=pivot?{headers:table.headers(),rows:table.visibleRows(),numeric:table.numeric()}:tradeExport(table);
    const csv=guardedCsv(metadata)+guardedCsv([shown.headers,...shown.rows],shown.numeric);void act(async()=>{try{await api('trading/blotter',lastReadRequest);}catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}downloadCsv('leanetrm-blotter.csv',csv);});
  };
  get('futures-refresh').onclick=()=>guarded(load);
  field('futures-contract').onchange=()=>{selectedContract=field('futures-contract').value;terms();};
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
  return {load,reset,sync,show,
    // A live correction reads the desk again, the panel with it unless the person has edits in it, and clears a risk run with a note.
    async liveRefresh(){
      const plan=liveRefreshPlan({panel:panel.current(),unsaved:editing(),riskRun:risk.shown()});
      // What the risk card holds belongs to the inputs the correction changed. It is cleared before the read, with the note when a run's
      // results were on it, so that a read that then fails leaves the note, which says what is gone, as it leaves the desk it had.
      risk.reset();
      if(plan.clearRisk)options.notice(plan.note!);
      await load(catalog?.kind??'directory',true);
      await draft.liveRefresh();
      if(plan.reloadPanel){await position.reload();await swapEditor.liveRefresh();}
    },
    refresh(){risk.refresh();draft.reset();editor.refresh();swapEditor.refresh();annotations.refresh();captureFilters();render();},
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
    // What the Blotter shows: its risk results, the trade in its panel, or the view its last read applied.
    report:()=>({risk:!get('risk-card').hidden,trade:shownTrade(),
      view:{mode:view.mode,bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds],groupBy:[...view.groupBy],measures:[...view.measures]}}),
    // A booking or editor with edits not yet sent, or a trade request or risk job whose outcome is unconfirmed.
    unsaved:()=>!!pending.command||!get('futures-booking').hidden&&bookingEdits.edited()||editing()||risk.unconfirmed(),
    // Whether the panel holds an amendment with edits, and the question asked before it is given up: false means the person kept it. The forms
    // are dropped without a question when the page is left on the person's word, as the assistant's Go anyway is.
    editing,leave,dropForms};
}
