import {draftValuation} from './draft-valuation.js';
import {amountText,type DisplayPrecision} from './amount-display.js';
import {tradeAnnotations,type PartyCatalog,type Booking,type Trader} from './trade-annotations.js';
import {setupSwapEditor} from './swap-editor.js';
import {setupTradeEditor} from './trade-editor.js';
import {futuresContractDescription,futuresContractKey,type FuturesContract as Contract} from './futures-contract.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {knownTradeRejection} from './trade-requests.js';
import {setupRisk} from './risk-view.js';
import {futuresInstrumentEditor} from './futures-instrument-editor.js';
import {createTableWorkspace} from './table-workspace.js';
import {renderDataGrid} from './data-grid.js';
import {pivotMatrix,pivotChartPanels,type PivotGroup} from './analytics-model.js';
import {renderChart,clearChart,type ChartSpec} from './chart-view.js';
import {renderRegularTable} from './regular-table.js';
import {trackEdits} from './edit-tracking.js';
import type {BlotterSelection} from './assistant-pages.js';
import {isPriceMeasure,priceMeanPolicy,blotterColumns,groupChoices,measureChoices,newBlotterView,parseBlotterViews,type BlotterView} from './blotter-layout.js';
import {guardedCsv,downloadCsv} from './csv-export.js';
import {friendly,publicText,displayInstant,deliveryLabel,curveName} from './market-view.js';

type Context={contextId:string;effectiveOn:string;knownAt:string};
type ReadRequest={snapshotId:string;convention:string;knowledgeMode:'current'|'retained';contextId?:string};
type BookingCatalog='directory'|'priced_contracts';
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string;portfolio_id:string}[];context:Context;asOf:string;canBook:boolean;canAmendTrades:boolean;bookingUnavailableReason:string|null}&({kind:'directory'}|{kind:'priced_contracts';contracts:Contract[]});
type Holdings={convention:string;pivotNumericalPolicy:string;groups:PivotGroup[];context:Context;asOf:string;evaluatedContracts:number;hasSwaps:boolean;holdings:{marketLabel?:string|null;booking:Booking;priceTick?:string;markTick?:string;fixedAmountFormat:DisplayPrecision;quoteMultiplier?:string;amountFormat:DisplayPrecision;instrumentType:string;traderId:string|null;trader:string|null;traderCode:string|null;memo:string|null;fixedPrice:string|null;presentValue:string|null;tradeReference:string;instrumentReference:string;tradeId:string;bindingId:string;bookId:string;contractId:string;revision:string;book:string;portfolio:string;curve:string;lots:string;quantity:string;unit:string;executedAt:string;executionPrice:string;mark:string;executionPnl:string;currency:string;deliveryFrom:string;deliveryTo:string;settlement:string}[];totals:{book:string;currency:string;executionPnl:string}[]};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;

export function setupFutures(options:{newPpa:()=>Promise<void>;api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRunRisk:()=>boolean;bookingUnavailableReason:()=>string;zone:()=>string;profile:()=>string;onReadContext:(at:string,known:string)=>void;source:()=>{convention:string;snapshotId:string;context?:{contextId?:string}}}){
  const {api,act,busy}=options;
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
  const workspace=createTableWorkspace(get('blotter-workspace'),{
    key:'blotter',rowMode:'trades',rowLabel:'Individual trades',dimensions:groupChoices,measures:measureChoices,
    pivotHelp:'Totals keep instrument types, quantity specifications and currencies separate. Blank measures are unavailable. Lot totals require dated-contract grouping. Price measures keep quote multipliers separate; averages use 18 decimal places, ties to even. Changing display mode clears row filters.',
    scopes:[['portfolios','Portfolios'],['books','Books']],actions:[['risk','Shocks'],['refresh','Refresh'],['export','Export'],['new','New trade']],onApply:()=>void act(load),
  });
  // Existing booking controls share the table toolbar.
  get('blotter-refresh').id='futures-refresh';get('blotter-new').id='futures-new';get('blotter-rows').id='futures-holdings';
  let catalog:Catalog|undefined,holdings:Holdings|undefined,owner='',snapshot='',selectedContract='',uncertain=false,generation=0;
  const pricedCatalog=()=>catalog?.kind==='priced_contracts'?catalog:undefined;
  const pending=new PendingCommand();
  const annotations=tradeAnnotations('futures',options.zone);
  const instrument=futuresInstrumentEditor(get('futures-instrument'),{contract:'futures-contract',lots:'futures-lots',price:'futures-execution',terms:'futures-contract-terms'});
  const draft=draftValuation(get('futures-draft-valuation'),{product:'futures',api,act,busy,denied:clear,key:()=>JSON.stringify(readContext()),draft:async()=>{
    const c=pricedCatalog()?.contracts.find(c=>contractKey(c)===field('futures-contract').value);if(!c)throw Error('Choose an exact dated futures contract.');
    return{context:readContext(),curve:c.curve,contractId:c.id,lots:field('futures-lots').value,executionPrice:field('futures-execution').value};
  }});
  get('futures-form').addEventListener('input',draft.reset);
  const bookingEdits=trackEdits(get('futures-form'));
  let lastReadRequest:unknown;
  let chartState:{panels:ChartSpec[];request:{context:unknown;view:unknown}}|undefined;
  let view=newBlotterView(),savedViews:BlotterView[]=[],table:ReturnType<typeof renderDataGrid>|undefined,tableColumns:string[]=[];
  let versions=new Map<string,string>();
  let draftBase:{name:string;version:string}|undefined;
  let viewsLoaded=false;
  const risk=setupRisk({api,act,busy,zone:options.zone,profile:options.profile,canRun:()=>options.canRunRisk()&&!!holdings,onDenied:clear,scope:()=>{
    if(!lastReadRequest)throw Error('Load the blotter before running risk.');
    return {context:{...(lastReadRequest as {context:Record<string,unknown>}).context},bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds]};
  }});
  get('blotter-risk').onclick=()=>void act(risk.open);
  function readContext(){const source=options.source();return {snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})};}
  const editor=setupTradeEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,
    queue:async command=>{if(pending.command)throw Error('Reconcile the retained request before submitting another command.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);},
    retry:()=>get<HTMLButtonElement>('futures-retry').click(),reconcile:()=>get<HTMLButtonElement>('futures-reconcile').click()});

  const swapEditor=setupSwapEditor({api,act,busy,zone:options.zone,context:readContext,pending:()=>pending.command,onDenied:clear,
    queue:async command=>{if(pending.command)throw Error('Reconcile the retained request first.');pending.command=structuredClone(command);uncertain=false;store();sync();await send(command);}});
  const kindDialog=get<HTMLDialogElement>('trade-kind');
  get('trade-kind-close').onclick=()=>kindDialog.close();
  get('trade-kind-ppa').onclick=()=>{kindDialog.close();void act(options.newPpa);};
  get('trade-kind-future').onclick=()=>{kindDialog.close();void act(async()=>{await load('priced_contracts');get('futures-booking').hidden=false;bookingEdits.clear();field('futures-book').focus();});};
  get('trade-kind-swap').onclick=()=>{kindDialog.close();get('futures-booking').hidden=true;void act(swapEditor.openNew);};
  const selected=(id:string)=>Array.from(get<HTMLSelectElement>(id).selectedOptions,o=>o.value);
  function choices(id:string,rows:{id:string;name:string}[],ids:string[]){
    const select=get<HTMLSelectElement>(id);select.replaceChildren(...rows.map(r=>new Option(publicText(r.name),r.id,false,ids.includes(r.id))),...ids.filter(id=>!rows.some(r=>r.id===id)).map(id=>new Option('Unavailable saved selection',id,false,true)));
  }
  function captureFilters(){if(!table)return;const state=table.state();view.query=state.query;view.columnFilters=Object.fromEntries(state.columns.flatMap((value,i)=>value===null?[]:[[tableColumns[i],value]]));}
  function readView():BlotterView{
    captureFilters();if(table?.ready())view.grid=table.layout();const next=structuredClone(view);next.mode=field('blotter-mode').value as BlotterView['mode'];
    if(get<HTMLSelectElement>('blotter-books').options.length)next.bookIds=selected('blotter-books');
    if(get<HTMLSelectElement>('blotter-portfolios').options.length)next.portfolioIds=selected('blotter-portfolios');
    next.splitBy=Array.from(get('blotter-split-dimensions').querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value);
    next.chart={visible:get<HTMLInputElement>('blotter-show-chart').checked,measure:field('blotter-chart-measure').value,kind:field('blotter-chart-kind').value as BlotterView['chart']['kind']};
    next.groupBy=Array.from(get('blotter-dimensions').querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value).concat(next.splitBy);
    next.measures=Array.from(get('blotter-measures').querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value);
    if(view.mode!==next.mode||JSON.stringify([view.groupBy,view.splitBy,view.measures])!==JSON.stringify([next.groupBy,next.splitBy,next.measures]))relayout(next);
    if(next.mode==='trades'){next.splitBy=[];next.groupBy=[];next.measures=[];}
    return next;
  }

  // A changed display mode, grouping or measure starts its own columns and drops the filters of the last one.
  function relayout(next:BlotterView){next.columns=next.mode==='pivot'?[...next.groupBy,...next.measures,'instrumentType','unit','currency',...(next.measures.some(isPriceMeasure)?['priceUnit']:[])]:newBlotterView().columns;next.query='';next.columnFilters={};next.grid={order:[],widths:{},sort:[]};}
  function viewControls(){
    field('blotter-mode').value=view.mode;get('blotter-pivot-fields').hidden=view.mode!=='pivot';
    choices('blotter-books',catalog?.books.map(b=>({id:b.id,name:b.name}))??[],view.bookIds);
    choices('blotter-portfolios',Array.from(new Map(catalog?.books.map(b=>[b.portfolio_id,{id:b.portfolio_id,name:b.portfolio}])??[]).values()),view.portfolioIds);
    workspace.pivot(view.groupBy,view.measures,view.splitBy);get<HTMLInputElement>('blotter-show-chart').checked=view.chart.visible;field('blotter-chart-measure').value=view.chart.measure;field('blotter-chart-kind').value=view.chart.kind;
    get('blotter-chart-card').querySelectorAll<HTMLButtonElement>('[data-plot-kind]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.plotKind===view.chart.kind)));
    choose('blotter-saved',savedViews.map(v=>({id:v.name,name:v.name})),field('blotter-saved').value||draftBase?.name||'','Current view · not saved');
  }
  async function layoutApi<T>(path:string,body?:unknown):Promise<T>{
    try{return await api<T>(path,body);}catch(error){
      if((error as {code?:string}).code==='access_denied'){savedViews=[];versions.clear();viewsLoaded=false;viewControls();clear();}
      throw error;
    }
  }
  async function loadViews(){
    const saved=await layoutApi<{version:string;layout:BlotterView}[]>('table-views/blotter');
    const parsed=parseBlotterViews(JSON.stringify(saved.map(v=>v.layout)));
    savedViews=parsed;versions=new Map(saved.map(v=>[v.layout.name,v.version]));viewsLoaded=true;viewControls();
  }

  const storageKey=()=>`openetrm.futures.request.server_booking_utc_v1.${options.profile()}`;
  function store(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The current visit still retains the exact in-memory request. */}}
  function restore(){
    if(owner===options.profile())return;
    owner=options.profile();pending.clear();uncertain=false;view=newBlotterView();savedViews=[];versions.clear();draftBase=undefined;viewsLoaded=false;field('blotter-name').value='';text('blotter-view-note','Named views are saved to your account. Dates and market inputs are selected separately.');get('blotter-column-choices').replaceChildren();text('blotter-table-title','Individual trades');workspace.closeMenus();viewControls();
    try{const saved=JSON.parse(sessionStorage.getItem(storageKey())??'null');if(saved&&['futures/book','futures/amend','swaps/book','swaps/amend'].includes(saved.path)&&typeof saved.operationId==='string'&&saved.body&&typeof saved.body==='object'){pending.command=saved;uncertain=true;}}catch{/* An unreadable local draft grants no authority. */}
  }
  function sync(){instrument.sync(busy()||!pricedCatalog());draft.sync();risk.sync();editor.sync();swapEditor.sync();get('blotter-risk').hidden=false;get('blotter-risk').textContent='Portfolio shocks';workspace.busy(busy());text('futures-booking-availability',pending.command?'A trade request is retained. Reconcile or retry it before creating another.':catalog?.canBook?'Working data · booking is enabled at the selected As of time.':catalog?.bookingUnavailableReason??options.bookingUnavailableReason());get('futures-new').setAttribute('aria-describedby','futures-booking-availability');get<HTMLFieldSetElement>('futures-fields').disabled=busy()||!pricedCatalog();get('futures-form').hidden=!pricedCatalog();get<HTMLButtonElement>('futures-book-submit').disabled=busy()||!pricedCatalog()?.canBook;get('futures-pending').hidden=!pending.command;get<HTMLButtonElement>('futures-new').disabled=busy()||!catalog||!!pending.command;get<HTMLButtonElement>('blotter-export').disabled=busy()||!table?.ready();get<HTMLButtonElement>('futures-retry').disabled=busy()||!(pending.command?.path.endsWith('/amend')?catalog?.canAmendTrades:catalog?.canBook);get<HTMLButtonElement>('futures-reconcile').disabled=busy();if(view.mode==='pivot')get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(['instrumentType','unit','currency','priceUnit'].includes(n.value))n.disabled=true;});}
  function clear(){instrument.clear();generation++;chartState=undefined;draft.reset();editor.reset();swapEditor.reset();if(kindDialog.open)kindDialog.close();risk.reset();catalog=undefined;holdings=undefined;table=undefined;lastReadRequest=undefined;choose('futures-contract',[],'');get('blotter-chart').querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(get('blotter-chart'));get('blotter-chart-card').hidden=true;text('header-calculation-time','');get('futures-booking').hidden=true;get('blotter-books').replaceChildren();get('blotter-portfolios').replaceChildren();workspace.clear();get('futures-totals').replaceChildren();text('futures-summary','Choose a market date to inspect holdings.');text('futures-terms','Select a dated contract to see its terms.');sync();}
  function reset(){selectedContract='';clear();restore();savedViews=[];versions.clear();viewsLoaded=false;viewControls();snapshot='';get<HTMLFormElement>('futures-form').reset();get('futures-pending').hidden=true;}
  function choose(id:string,rows:{id:string;name:string}[],selected:string,placeholder='Choose…'){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option(placeholder,''),...rows.map(r=>new Option(publicText(r.name),r.id)));select.value=rows.some(r=>r.id===selected)?selected:'';}
  const contractKey=futuresContractKey;
  function terms(){const c=pricedCatalog()?.contracts.find(c=>contractKey(c)===field('futures-contract').value);text('futures-terms',c?futuresContractDescription(c,options.zone()):'Select a dated contract to see its terms.');instrument.render(c,options.zone(),(value,tick)=>amountText(value,'price',tick));}
  function render(){
    if(!catalog||!holdings)return;
    annotations.populate(catalog.bookingActor,catalog,annotations.read());
    choose('futures-book',catalog.books.map(b=>({id:b.id,name:`${b.portfolio} · ${b.name}`})),field('futures-book').value);
    instrument.populate(pricedCatalog()?.contracts??[],selectedContract);terms();
    if(pricedCatalog())selectedContract=field('futures-contract').value;
    const scope=options.profile()+':'+snapshot+':'+catalog.context.contextId;
    const pivot=view.mode==='pivot';
    const columns=blotterColumns.filter(([key])=>pivot?[...view.groupBy,...view.measures,'instrumentType','unit','currency',...(view.measures.some(isPriceMeasure)?['priceUnit']:[])].includes(key):key!=='tradeCount'&&!isPriceMeasure(key));
    tableColumns=columns.map(c=>c[0]);
    const rows:Record<string,string|null>[]=pivot?holdings.groups.map(g=>Object.fromEntries(columns.map(([key])=>[key,key==='instrumentType'?friendly(g.instrumentType):key==='market'?String(g.marketLabel??curveName(g.market)):key==='contract'?deliveryLabel(String(g.deliveryFrom),String(g.deliveryTo)):String(g[key]??'')]))):holdings.holdings.map(({booking,amountFormat,fixedAmountFormat,...h})=>({...h,priceUnit:`${h.currency}/${h.unit}${h.quoteMultiplier&&h.quoteMultiplier!=='1'?' × '+h.quoteMultiplier:''}`,tradeDate:booking.tradeDate,legalEntity:booking.legalEntity??null,counterparty:booking.counterparty??null,instrumentType:friendly(h.instrumentType),market:h.marketLabel??curveName(h.curve),contract:deliveryLabel(h.deliveryFrom,h.deliveryTo),executed:displayInstant(h.executedAt,options.zone()),settlement:publicText(h.settlement==='unprocessed'?'Unprocessed':h.settlement)}));
    const visible=columns.map(([key])=>view.columns.includes(key)||pivot&&['instrumentType','unit','currency','priceUnit'].includes(key));
    if(!visible.some(Boolean))visible[0]=true;
    const numeric=tableColumns.flatMap((key,i)=>(['tradeCount','lots','quantity','executionPrice','mark','executionPnl','fixedPrice','presentValue','revision'].includes(key)||isPriceMeasure(key))?[i]:[]);
    const matrix=pivot&&view.splitBy.length?pivotMatrix(holdings.groups,view.groupBy,view.splitBy,view.measures):undefined;
    const gridColumns=matrix?.columns??columns.map(([key,title],i)=>({key,title,numeric:numeric.includes(i)}));
    if(matrix){tableColumns=matrix.columns.map(c=>c.key);}
    const gridVisible=gridColumns.map(c=>c.key.startsWith('pivot:')?view.columns.includes((c as {measure:string}).measure):view.columns.includes(c.key)||pivot&&['instrumentType','unit','currency','priceUnit'].includes(c.key));
    const rawRows=matrix?.rows??rows;
    const displayRows=rawRows.map((row,index)=>gridColumns.map(c=>{
      const value=row[c.key]??'';if(!c.numeric||value==='')return value;
      const measure=c.key.startsWith('pivot:')?(c as {measure:string}).measure:c.key;
      const original=pivot?undefined:holdings!.holdings[index];
      if(['executionPrice','mark','fixedPrice'].includes(measure)||isPriceMeasure(measure))return amountText(value,'price',measure==='mark'?original?.markTick:original?.priceTick,measure==='fixedPrice'?original?.fixedAmountFormat:original?.amountFormat);
      if(['presentValue','executionPnl'].includes(measure))return amountText(value,'money',undefined,original?.amountFormat);
      if(measure==='quantity')return amountText(value,'quantity',undefined,original?.amountFormat);
      return value;
    }));
    table=workspace.render('futures-holdings',scope+JSON.stringify([view.mode,view.groupBy,view.measures,view.bookIds,view.portfolioIds,view.query,view.columnFilters]),gridColumns.map(c=>c.title),rawRows.map(row=>gridColumns.map(c=>row[c.key]??'')),gridColumns.flatMap((c,i)=>c.numeric?[i]:[]),busy,{displayRows,keys:tableColumns,columnGroups:matrix?Object.fromEntries(matrix.columns.filter(c=>c.groupKey).map(c=>[c.key,{key:c.groupKey!,title:c.groupTitle!,measureTitle:c.measureTitle!}])):undefined,layout:view.grid,onLayout:layout=>{view.grid=layout;},onReady:sync,visible:gridVisible,initial:{query:view.query,columns:tableColumns.map(key=>view.columnFilters[key]??null)},retainMissingFilters:true,onRowOpen:pivot?undefined:index=>{const row=holdings?.holdings[index];if(row)void act(()=>row.instrumentType==='financial_swap'?swapEditor.open(row.tradeId,Number(row.revision)):editor.open(row.tradeId,Number(row.revision)));}});

    workspace.columns(columns,columns.filter((_,i)=>visible[i]).map(c=>c[0]),selected=>{
      if(busy())return;
      if(!selected.length){render();return;}captureFilters();if(table?.ready())view.grid=table.layout();view.columns=selected;render();
    },pivot?['instrumentType','unit','currency','priceUnit']:[]);
    if(pivot)get('blotter-column-choices').querySelectorAll<HTMLInputElement>('input').forEach(n=>{if(['instrumentType','unit','currency','priceUnit'].includes(n.value)){n.checked=true;n.disabled=true;}});
    renderRegularTable(get('futures-totals'),'futures-totals',scope,['Book','Currency','Execution P&L'],holdings.totals.map(t=>[t.book,t.currency,t.executionPnl]),[2],busy,{displayRows:holdings.totals.map(t=>[t.book,t.currency,amountText(t.executionPnl,'money')])});
    workspace.summary([{label:'Trades · selected books',value:String(holdings.holdings.length)},{label:'Futures prices evaluated',value:String(holdings.evaluatedContracts)},
      ...holdings.totals.map(t=>({label:`${t.book} · execution P&L`,value:`${amountText(t.executionPnl,'money')} ${t.currency}`,signed:t.executionPnl}))]);
    text('blotter-table-title',pivot?view.splitBy.length?'Pivot table':'Pivot summary':'Individual trades');
    workspace.describe(pivot?[
      {label:'Rows',values:view.groupBy.filter(d=>!view.splitBy.includes(d)).map(d=>groupChoices.find(c=>c[0]===d)![1])},
      {label:'Columns',values:view.splitBy.map(d=>groupChoices.find(c=>c[0]===d)![1])},
      {label:'Values',values:view.measures.map(m=>measureChoices.find(c=>c[0]===m)![1])},
    ]:[]);
    text('futures-summary',`${holdings.holdings.length} trade bindings · ${holdings.evaluatedContracts} evaluated futures contract prices. Financial swaps are valued from their side schedules.`);options.onReadContext(holdings.asOf,holdings.context.knownAt);sync();
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
    if(live){generation++;risk.reset();}else clear();
    const started=generation,scope=JSON.stringify([options.profile(),source]);
    const request:ReadRequest={snapshotId:source.snapshotId,convention:source.convention,knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})};
    try{
      const selectedView={mode:view.mode,bookIds:view.bookIds,portfolioIds:view.portfolioIds,groupBy:view.mode==='pivot'?view.groupBy:[],measures:view.mode==='pivot'?view.measures:[],pivotConvention:view.mode==='pivot'?'trading_blotter_totals_v1':null,priceNumericalPolicy:view.mode==='pivot'&&view.measures.some(m=>isPriceMeasure(m)&&m.endsWith('Mean'))?priceMeanPolicy:null};
      const combined=await api<{catalog:Catalog;blotter:Holdings}>('trading/workspace',{context:{...request,convention:'trading_workspace_v1'},view:selectedView,bookingCatalog});
      requireCurrentRead(started,scope);
      const selected=combined.catalog.context,reported=combined.blotter.context;
      if(combined.catalog.kind!==bookingCatalog||combined.catalog.kind==='directory'&&Object.hasOwn(combined.catalog,'contracts')||combined.catalog.kind==='priced_contracts'&&!Array.isArray(combined.catalog.contracts))throw Error('The trading workspace returned an inconsistent booking catalog. Refresh and try again.');
      if(!selected.contextId||selected.contextId!==reported.contextId||selected.effectiveOn!==reported.effectiveOn||selected.knownAt!==reported.knownAt||combined.catalog.asOf!==combined.blotter.asOf)throw Error('The trading workspace returned inconsistent market contexts. Refresh and try again.');
      catalog=combined.catalog;holdings=combined.blotter;viewControls();
      const reportRequest={context:{...request,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:selected.contextId},view:selectedView};lastReadRequest=reportRequest;render();await loadChart(reportRequest,started,scope);requireCurrentRead(started,scope);workspace.closeMenus();
    }catch(error){if(started===generation&&(!live||(error as {code?:string}).code==='access_denied'))clear();throw error;}
  }
  async function loadChart(reportRequest:{context:unknown;view:unknown},started:number,scope:string){
    if(!view.chart.visible)return;
    const dimensions=view.mode==='pivot'?view.groupBy:['book','market','contract'],splitBy=view.mode==='pivot'?view.splitBy:['contract'];
    const chartRequest={context:reportRequest.context,view:{mode:'pivot',bookIds:view.bookIds,portfolioIds:view.portfolioIds,groupBy:dimensions,measures:[view.chart.measure],pivotConvention:'trading_blotter_totals_v1',priceNumericalPolicy:isPriceMeasure(view.chart.measure)&&view.chart.measure.endsWith('Mean')?priceMeanPolicy:null}};
    const report=await api<Holdings>('trading/blotter',chartRequest);requireCurrentRead(started,scope);
    text('blotter-chart-scope',view.mode==='pivot'?'Follows the pivot rows and columns · selected books and portfolios, before table row filters.':'By dated contract and book / market · selected books and portfolios, before table row filters.');
    chartState={panels:pivotChartPanels(report.groups,dimensions,splitBy,view.chart.measure),request:chartRequest};renderChartPanels();
  }
  function renderChartPanels(){
    if(!chartState)return;const {panels,request}=chartState,host=get('blotter-chart');
    host.querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));clearChart(host);host.classList.remove('chart-empty');get('blotter-chart-card').hidden=false;
    get('blotter-chart-card').querySelectorAll<HTMLButtonElement>('[data-plot-kind]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.plotKind===view.chart.kind)));
    if(!panels.length){host.classList.add('chart-empty');text('blotter-chart','No values available for the selected books and portfolios.');}
    for(const spec of panels){
      const panel=document.createElement('div');panel.className='chart';host.append(panel);
      renderChart(panel,{...spec,kind:view.chart.kind,exportImage:download=>void act(async()=>{try{await api('trading/blotter',request);}catch(error){clear();throw error;}download();})});
    }
  }
  get('blotter-chart-card').querySelectorAll<HTMLButtonElement>('[data-plot-kind]').forEach(button=>button.onclick=()=>{if(busy())return;view.chart.kind=button.dataset.plotKind as BlotterView['chart']['kind'];field('blotter-chart-kind').value=view.chart.kind;renderChartPanels();});
  async function send(command:RequestCommand){
    const retrying=uncertain;uncertain=true;store();
    try{await api(command.path,{...command.body,operationId:command.operationId});}
    catch(error){
      // A rejected retry says nothing about the original uncertain outcome.
      if(!retrying&&knownTradeRejection((error as {code?:string}).code)){pending.clear();uncertain=false;store();}
      if(command.path==='futures/amend')editor.reset();if(command.path.startsWith('swaps/')&&!command.path.startsWith('swaps/settlement/'))swapEditor.reset();if((error as {code?:string}).code==='access_denied')clear();sync();throw error;
    }
    pending.clear();uncertain=false;store();get('futures-booking').hidden=true;await load('directory',command.path.startsWith('swaps/settlement/'));
  }
  get<HTMLFormElement>('futures-form').onsubmit=event=>{event.preventDefault();void act(async()=>{
    const priced=pricedCatalog();if(!priced?.canBook)throw Error('Return to current inputs with trading authority before booking.');
    const c=priced.contracts.find(c=>contractKey(c)===field('futures-contract').value);if(!c)throw Error('Choose the exact dated contract.');
    const body={...annotations.read(),snapshotId:options.source().snapshotId,convention:options.source().convention,knowledgeMode:'current',bookId:field('futures-book').value,curve:c.curve,contractId:c.id,
      lots:field('futures-lots').value,executionPrice:field('futures-execution').value};
    const command=pending.begin('futures/book',body);store();sync();await send(command);
  });};
  get('futures-retry').onclick=()=>void act(async()=>{if(!pending.command)throw Error('No retained booking to retry.');await send(pending.command);});
  get('futures-reconcile').onclick=()=>void act(async()=>{
    if(!pending.command)throw Error('No retained booking to reconcile.');
    try{
      const result=await api<{tradeId?:string}>(pending.command.path.startsWith('swaps/settlement/')?'swaps/settlement/status':pending.command.path.startsWith('swaps/')?'swaps/status':'futures/status',{operationId:pending.command.operationId});
      if(!result.tradeId)throw Error('No committed outcome is visible yet. Retry the original request with the same identity.');
      pending.clear();uncertain=false;store();get('futures-booking').hidden=true;await load();
    }catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}
  });
  get('futures-new').onclick=()=>{if(busy()||!catalog)return;kindDialog.showModal();};
  get('futures-cancel').onclick=()=>{if(!busy()){get('futures-booking').hidden=true;bookingEdits.clear();}};
  field('blotter-mode').onchange=()=>{if(busy())return;get('blotter-pivot-fields').hidden=field('blotter-mode').value!=='pivot';};
  get('blotter-apply').onclick=()=>void act(load);
  get('blotter-save').onclick=()=>void act(async()=>{
    const draft=readView();const next=parseBlotterViews(JSON.stringify([{...draft,name:field('blotter-name').value}]))[0];
    const saved=await layoutApi<{version:string;layout:BlotterView}>('table-views/blotter',{layout:next,expectedVersion:draftBase?.name===next.name?draftBase.version:versions.get(next.name)??null});
    versions.set(next.name,saved.version);draftBase={name:next.name,version:saved.version};savedViews=[...savedViews.filter(v=>v.name!==next.name),saved.layout];
    view=structuredClone(saved.layout);viewControls();field('blotter-saved').value=next.name;text('blotter-view-note','Saved to your account. Dates and market inputs are selected separately.');table=undefined;workspace.closeMenus();await load();
  });
  field('blotter-saved').onchange=()=>void act(async()=>{const saved=savedViews.find(v=>v.name===field('blotter-saved').value);if(!saved)return;draftBase={name:saved.name,version:versions.get(saved.name)!};table=undefined;view=structuredClone(saved);field('blotter-name').value=view.name;viewControls();workspace.closeMenus();await load();});
  get('blotter-views-reload').onclick=()=>void act(async()=>{
    const name=field('blotter-saved').value;
    await loadViews();
    const saved=savedViews.find(v=>v.name===name);
    if(saved){draftBase={name:saved.name,version:versions.get(saved.name)!};table=undefined;view=structuredClone(saved);field('blotter-name').value=view.name;viewControls();await load();}
    text('blotter-view-note',saved?'The selected account layout has been reloaded.':'Saved-view list refreshed. Choose a view to restore it.');
  });
  get('blotter-delete').onclick=()=>void act(async()=>{const name=field('blotter-saved').value;if(!name)throw Error('Choose a saved view to delete.');await layoutApi('table-views/blotter/delete',{name,expectedVersion:draftBase?.name===name?draftBase.version:versions.get(name)});if(draftBase?.name===name)draftBase=undefined;savedViews=savedViews.filter(v=>v.name!==name);versions.delete(name);viewControls();field('blotter-name').value='';text('blotter-view-note','Saved view deleted. The displayed data is unchanged.');});
  get('blotter-export').onclick=()=>{if(busy()||!table||!holdings||!lastReadRequest)return;const rows=table.visibleRows();const metadata=[['Blotter snapshot'],['As of',holdings.asOf],['Knowledge cutoff',holdings.context.knownAt],['Convention',holdings.convention],['Display',view.mode],['Grouping',view.mode==='pivot'?view.groupBy.join(' / '):'None'],['Measures',view.mode==='pivot'?view.measures.join(' / '):'Trade rows'],['Portfolios',view.portfolioIds.length?catalog!.books.filter(b=>view.portfolioIds.includes(b.portfolio_id)).map(b=>b.portfolio).filter((v,i,a)=>a.indexOf(v)===i).join(' / '):'All authorized portfolios'],['Books',view.bookIds.length?catalog!.books.filter(b=>view.bookIds.includes(b.id)).map(b=>b.name).join(' / '):'All books in selected portfolios'],['Search',table.state().query],['Column filters',JSON.stringify(Object.fromEntries(table.state().columns.flatMap((v,i)=>v===null?[]:[[tableColumns[i],v]])))],['Numerical policy',holdings.pivotNumericalPolicy],['Pivot convention',view.mode==='pivot'?'trading_blotter_totals_v1':'Not applicable'],[]];
    const csv=guardedCsv(metadata)+guardedCsv([table.headers(),...rows],table.numeric());void act(async()=>{try{await api('trading/blotter',lastReadRequest);}catch(error){if((error as {code?:string}).code==='access_denied')clear();throw error;}downloadCsv('leanetrm-blotter.csv',csv);});
  };
  get('futures-refresh').onclick=()=>void act(load);
  field('futures-contract').onchange=()=>{selectedContract=field('futures-contract').value;terms();};
  // A Blotter view a navigation target names, applied as Apply applies the view's controls.
  async function show(selection:BlotterSelection){
    restore();if(!viewsLoaded)await loadViews();
    const next:BlotterView={...structuredClone(view),mode:selection.mode,bookIds:[...selection.bookIds],portfolioIds:[...selection.portfolioIds],
      groupBy:[...selection.groupBy],splitBy:[],measures:[...selection.measures]};
    if(view.mode!==next.mode||JSON.stringify([view.groupBy,view.splitBy,view.measures])!==JSON.stringify([next.groupBy,next.splitBy,next.measures]))relayout(next);
    view=next;viewControls();await load();
  }
  return {load,reset,sync,show,async liveRefresh(){await load(catalog?.kind??'directory',true);await draft.liveRefresh();await editor.liveRefresh();await swapEditor.liveRefresh();},refresh(){risk.refresh();draft.reset();editor.refresh();swapEditor.refresh();annotations.refresh();captureFilters();render();},
    openTrade:(tradeId:string,instrumentType:'future'|'financial_swap',revision:number|null)=>instrumentType==='financial_swap'?swapEditor.open(tradeId,revision):editor.open(tradeId,revision),
    // What the Blotter shows: its risk results, an open trade, or the view its last read applied.
    report:()=>({risk:!get('risk-card').hidden,trade:editor.shown()??swapEditor.shown()??null,
      view:{mode:view.mode,bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds],groupBy:[...view.groupBy],measures:[...view.measures]}}),
    // A booking or editor with edits not yet sent, or a trade request or risk job whose outcome is unconfirmed.
    unsaved:()=>!!pending.command||!get('futures-booking').hidden&&bookingEdits.edited()||editor.unsaved()||swapEditor.unsaved()||risk.unconfirmed()};
}
