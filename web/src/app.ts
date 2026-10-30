import {refreshContextPanels} from './context-view.js';
import {setupPpa} from './ppa-editor.js';
import {setupCustomCurves} from './custom-curves-view.js';
import {LiveUpdates,liveContextUpdate,type LiveSelection,type LiveContextUpdate,type LiveStatus} from './live-context.js';
import {setupMarketCalendar,type MarketDates} from './market-calendar.js';
import {newestDate,newestClose,closeBadge,latestInstant,type CloseChoice} from './latest-close.js';
import {setupPowerImports} from './power-imports-view.js';
import {setupIntervalSeries} from './interval-series-view.js';
import {setupIntervalCurves} from './interval-curves-view.js';
import {setupJobs} from './jobs-view.js';
import {setupReferenceEditor} from './reference-editor.js';
import {setupReferenceNavigation,type ReferenceHit} from './reference-navigation.js';
import {parseReferenceAddress,formatReferenceAddress,type ReferenceAddress} from './reference-address.js';
import {setupPartyEditors} from './party-view.js';
import {amountText,prepareAmountDisplay} from './amount-display.js';
import {withNotice} from './notice-text.js';
import {setupUnitConverter} from './units-view.js';
import {renderChart,clearChart} from './chart-view.js';
import {setupFutures} from './futures-view.js';
import {bookingFallback} from './new-trade.js';
import {setupPowerView} from './power-view.js';
import {setupAdministration} from './admin-view.js';
import {setupAgentGrants} from './agent-grants.js';
import {setupAiSettings,entryRanges} from './ai-settings.js';
import {agentRefusal,aiTabs} from './agent-settings.js';
import {type Context,setupDateEditor} from './date-editor.js';
import {renderRegularTable,clearTableFilters} from './regular-table.js';
import {curveWorkbook,rateWorkbook,renderWorkbook} from './curve-workbook.js';
import {type PricingGraph,pricingGraph,pricingEquation} from './pricing-view.js';
import {renderPricingGraph} from './pricing-graph-view.js';
import {type Row,type Frame,type Workspace,type CurveResult,type Point,type ReferenceDisplay,type ReferenceRevision,displayReferenceRows,referenceLabel,presentedReferences,val,publicText,friendly,curveName,setCurveNames,exactRow,commodityName,displayInstant,deliveryMonth,frameOptions,quoteUnit,publicColumns,timeLabels,allCurves,displayDate,pointDate,pointIdentity,deliveryLabel,historyPriceLabel,summaryTiles,contractCountText} from './market-view.js';
import {dataSections,keylessCategories} from './data-sections.js';
import {renderReferenceItem} from './reference-detail.js';
import {renderFuturesProduct} from './futures-product-view.js';
import type {FuturesProductRead} from './futures-product-model.js';
import {type Preferences,newPreferences,loadPreferences,savePreferences,validTimeZone,zoneChoices} from './preferences.js';
import {setupShortcutEditor,scheduleErrorMessage} from './shortcut-view.js';
import {requestRefusal} from './request-refusal.js';
import {setupTouProducts} from './tou-products.js';
import {validateMarketNavigation,navigationGroups,navigationCategories,navigationCurveKind,type MarketNavigation,type FrameSummary} from './market-navigation.js';
import {navigationGroups as sidebarGroups,pageGroup,pageAvailable,firstPage} from './navigation-groups.js';
import {setupAssistant} from './assistant-panel.js';
import {pageReport,webPage,type PageState,type PageContext,type Place} from './assistant-pages.js';
import {readPages,type Target,type TargetContext} from './assistant-events.js';
import {readCurveShapes} from './curve-shapes.js';
import type {CurveShape} from './desk-model.js';
import {commodityDot,librarySparkline,marketBoard,renderBoard,shapeReads} from './market-board.js';
import {renderTile} from './tiles.js';
import {kindBadge} from './curve-kinds.js';
import {panelPlacement} from './side-panel.js';
import {answeredOverlays,type OverlayCatalog,type OverlayRequest,type OverlaySeries} from './time-chart.js';
import {type ContractHistory,type RevisionPolicy,revisionPolicies,historyBasis,historyChart,knownAtText,setupContractPanel} from './contract-panel.js';

const exampleId=new URLSearchParams(location.search).get('example')??'baseline';
if(!['baseline','exchange'].includes(exampleId))throw Error('Choose an available example.');
const apiRoot=exampleId==='exchange'?'/examples/exchange/api/':'/api/';
const $=<T extends HTMLElement=HTMLElement>(id:string):T=>{const node=document.getElementById(id);if(!node)throw Error('This page could not load. Please refresh.');return node as T;};
const select=(id:string)=>$<HTMLSelectElement>(id);
const groupNode=(id:string)=>$<HTMLDetailsElement>('nav-group-'+id);
const text=(id:string,value:string)=>{$(id).textContent=publicText(value);};
function el<K extends keyof HTMLElementTagNameMap>(tag:K,content?:string){const node=document.createElement(tag);if(content!==undefined)node.textContent=publicText(content);return node;}
function choose(id:string,options:{value:string;name:string}[],value:string){const node=select(id);node.replaceChildren(...options.map(o=>{const option=el('option',o.name);option.value=o.value;return option;}));node.value=value;}
let csrf='',workspace:Workspace|undefined,curve='',result:CurveResult|undefined,pricingContract='',showAllContracts=false,input='',draftFor='',busy=false,page='market';
let sourceOnlyCurve=false;
// The contract the panel shows, and its history: the revision policy (D237) and overlays the person chose, the read's answer and the
// overlays it returned, named from the catalog, or that the read failed. The panel opens on exact closes with no overlays.
let contractOpener:HTMLElement|undefined,contract='',historyPolicy:RevisionPolicy=revisionPolicies[0].policy,overlayRequests:OverlayRequest[]=[],historyData:ContractHistory|undefined,historySeries:OverlaySeries[]=[],historyFailed=false;
// The overlay catalog, read for one account.
let overlayCatalog:{scope:string;catalog:OverlayCatalog}|undefined;
let contextDirty=false,currentProfile='',administrationScope='',permissions:string[]=[];
let navigation:MarketNavigation|undefined,marketGeneration=0;
// The curve shapes the market board and the library draw, read at one close for one directory and account (markets-desk.md).
let boardShapes:{owner:MarketNavigation;scope:string;shapes:ReadonlyMap<string,CurveShape|string>}|undefined;
let liveSucceeded:Date|undefined;
let liveStream:EventSource|undefined,liveUpdates:LiveUpdates<LiveContextUpdate & {generation:number}>|undefined,liveId='',lastLiveUpdate:LiveContextUpdate|undefined;
type ReferencePage={table:string;rows:Row[];references:Record<string,Row[]>;referenceDisplay?:ReferenceDisplay|null;offset:number;pageSize:number;maximumOffset:number;hasMore:boolean;search:string;referenceRevision:{catalogId:string;revisionId:string};definitionRevisionId:string;context:Context|null;profile:string;referenceScope:'selected_table'};
let referencePage:ReferencePage|undefined;
// The reference category tab chosen; empty until one is chosen, when the page opens on Currencies.
let referenceTable='';
// The address of the reference list or item shown, and the list an item was opened from, which its breadcrumb returns to.
let shownReference='',referenceReturn:{table:string;search:string;offset:number}|undefined;
const canAdvanceReference=()=>!!referencePage&&referencePage.hasMore&&referencePage.offset+referencePage.pageSize<=referencePage.maximumOffset;
let hostedPilot=false;
type ProfileChoices={mode:string;profiles:{id:string;name:string}[];customProfiles:{id:string;name:string}[]};
type StartupAccess={profile:string;permissions:string[]};
const can=(permission:string)=>permissions.includes(permission);
const historicalContext=()=>navigation?.context??undefined;
const canCorrect=()=>can('ImportObservations')&&!historicalContext();
let prefs:Preferences=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);
let marketDateGeneration=0,marketDatesLoaded=false;
// The workspace's newest close (D219): the last availability read for the whole workspace, the latest instant it had when
// the close now shown was selected, whether a newer-close check is due and whether one is running, and the close the page
// shows, which the As of select is on at every moment: a close that has been chosen is applied only once the person agrees.
let workspaceCloses:MarketDates|undefined,closeBasis:{close:string;latest:string}|undefined,closeCheckDue=false,checkingCloses=false,shownClose='',sessionEnded=false;
const data=()=>{if(!workspace)throw Error('Market data is still loading.');return workspace;};
const directory=()=>{if(!navigation)throw Error('Market navigation is still loading.');return navigation;};
const frame=()=>{const found=directory().frames.find(f=>f.id===select('snapshot').value);if(!found)throw Error('Choose a market date above.');return found;};
const pricingFrame=()=>{const found=data().frames.find(f=>f.id===frame().id);if(!found)throw Error('The selected market inputs are missing. Refresh and try again.');return found;};
const selectedCurve=()=>exactRow(allCurves(data()),'code',curve);
const listedCurve=()=>{const found=directory().curves.find(c=>c.code===curve);if(!found)throw Error('Some market details are missing or inconsistent. Refresh and try again.');return found;};
const isRates=()=>!!curve&&listedCurve().marketType==='rates';
const row=(table:string,key:string,value:string,reference=false)=>exactRow((reference?data().references:data().definitions)[table],key,value);
const observation=(id:string)=>exactRow(data().observations,'id',id);
const instant=(value:string)=>displayInstant(value,prefs.timeZone);
const unit=(quote:string)=>quoteUnit(quote,data());
function notice(message:string){const shown=$('notice');text('notice',withNotice(shown.hidden?'':shown.textContent??'',publicText(message)));shown.hidden=false;}
function persist(){try{savePreferences(localStorage,(exampleId==='baseline'?currentProfile:exampleId+':'+currentProfile),prefs);}catch{notice('Your changes work for this visit, but this browser could not save your preferences.');}}
function loadProfile(){clearTableFilters();try{prefs=loadPreferences(localStorage,(exampleId==='baseline'?currentProfile:exampleId+':'+currentProfile),Intl.DateTimeFormat().resolvedOptions().timeZone);}catch{prefs=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);notice('Your saved profile could not be loaded. This visit uses your browser’s time zone. You can save your preferences in Profile.');}}
function syncControls(){
  renderWorkspaceMode();
  document.querySelectorAll<HTMLButtonElement|HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>('button,input,select,textarea').forEach(n=>n.disabled=busy);
  if(!busy){$('save').toggleAttribute('disabled',!canCorrect());select('snapshot').disabled=['admin','ai'].includes(page);$('follow-live').toggleAttribute('disabled',!select('snapshot').value);}
  for(const id of ['source-contract','pricing-contract'])select(id).disabled=busy||showAllContracts||!result?.points.length;
  for(const id of ['source-all','pricing-all'])$<HTMLButtonElement>(id).disabled=busy||!result||!result.points.length||(result.points.length<2&&!showAllContracts);
  $('reference-previous').toggleAttribute('disabled',busy||!referencePage||referencePage.offset===0);$('reference-next').toggleAttribute('disabled',busy||!canAdvanceReference());
  futures.sync();parties.sync();agentGrants.sync();jobs.sync();aiSettings.sync();referenceEditor.sync();dateEditor.sync();deliveryPrices.sync();intervalSeries.sync();intervalCurves.sync();ppa.sync();marketCalendar.sync();renderCloseBadge();if(initialWorkspaceReady)customCurves.sync();assistant.sync();contractPanel.place();customCurves.place();refreshContextPanels();document.body.setAttribute('aria-busy',String(busy));
}
function renderWorkspaceMode(){
  const historical=!!historicalContext();
  $('date-context').dataset.contextMode=contextDirty?'draft':historical?'retained':'current';
  text('workspace-mode-label',contextDirty?'Unapplied historical selection':navigation?.live?'Live corrections · read only':historical?'Historical view · read only':'Working data · current knowledge');
  $('return-working').hidden=!historical&&!contextDirty;
  $('follow-live').hidden=historical||contextDirty||!['market','custom-curves','futures'].includes(page);
  $('follow-live').toggleAttribute('disabled',busy||!select('snapshot').value);
  const today=new Intl.DateTimeFormat('en',{timeZone:prefs.timeZone,year:'numeric',month:'short',day:'numeric'}).format(new Date());
  text('workspace-today',`Today: ${today} · ${prefs.timeZone}. The As of selector shows available prices; working data can have an earlier market date.`);
}
async function transport<T>(path:string,body?:unknown,root=apiRoot,signal?:AbortSignal):Promise<T>{
  let response:Response;
  try{response=await fetch(root+path,{method:body===undefined?'GET':'POST',credentials:'same-origin',headers:body===undefined?{}:{'Content-Type':'application/json','X-Preview-CSRF':csrf},body:body===undefined?undefined:JSON.stringify(body),signal:signal?AbortSignal.any([signal,AbortSignal.timeout(20000)]):AbortSignal.timeout(20000)});}
  catch{throw Object.assign(Error(body!==undefined?'We could not confirm the result. Refresh and check the saved changes before saving again.':'The app could not connect. Please refresh the page.'),{code:'response_unknown'});}
  if(response.status===401){sessionEnded=true;workspace=undefined;result=undefined;historyData=undefined;curve='';document.body.hidden=true;location.replace('/auth');throw Object.assign(Error('Sign in to continue.'),{code:'sign_in_required'});}
  if(!response.ok){const error=await response.json().catch(()=>({code:'request_failed'}));if(path.startsWith('swaps/settlement/'))throw Object.assign(Error(error.message??'The settlement operation was refused.'),{code:error.code});if(path.startsWith('ppa/'))throw Object.assign(Error(`${error.code??'request_failed'}: ${error.message??'The PPA operation was refused.'}`),{code:error.code,details:error});const messages:Record<string,string>={index_fixing_convention_missing:'This index has no qualified fixing schedule for the selected observations. Its reference rules must be configured before this swap can be priced.',index_fixing_missing:'A published index fixing is required but is missing from the selected market inputs. Load the fixing and select a complete market context.',index_fixing_definition:'The fixing schedule does not match the exact index, price units or source definition.',swap_fixing_read_only:'Fixing dates come from the selected index convention and cannot be entered manually.',reference_edit:'Choose a reference row and complete its supported display fields. Financial terms are read-only.',reference_conflict:'This reference version changed. Reload before editing or reconcile the retained request.',reference_limit:'This reference catalog exceeds the supported metadata authoring limits.',reference_economic_change:'The saved version changes financial terms and cannot be applied as a display update.',reference_outcome_unknown:'The save outcome is unconfirmed. Inspect versions or retry the retained original request.',missing_curve_view:'This curve has no configured close for the selected date. Choose an available calendar date.',missing_observation:'Required market data is missing for this curve and close. Choose another available date or load the missing prices.',recipe_definition:'Complete every required operation setting and connect each node to the curve output with compatible inputs.',recipe_input:'The selected dates or contracts do not have complete valid observations. Check the source and date window.',recipe_transform:'This saved curve uses an operation that is unavailable in the connected engine. Recreate it with supported graph operations.',recipe_formula:'Use quoted parameters, connected @node inputs and typed JSON contract mappings.',custom_curve_definition:'Complete the curve operations, connections and required settings. Check the selected sources and date coverage.',custom_curve_conflict:'This curve changed or the code is already used. Reload the library before editing.',custom_curve_coverage:'The selected dates or contracts do not have complete observations. Choose a covered window or load the required prices.',custom_curve_operator:'This curve uses an unsupported operation or incompatible input connection.',custom_curve_source:'Choose an available curve from the selected market context.',custom_curve_limit:'This curve exceeds the supported graph or history bounds.',futures_execution_policy:'Trading eligibility is missing, ambiguous or inconsistent with its source. A maintainer must qualify this contract before it can be priced.',unit_catalog:'The configured unit catalog is unavailable at this effective date or knowledge cutoff.',reference_catalog:'The configured reference catalog is unavailable in this context.',reference_page:'Choose an available category and valid page or search text.',clock_tzdb:'The installed time-zone rules differ from the product clock’s declared release, or cannot be verified. Calculations are blocked until they match.',clock_rule:'This product’s market clock is incomplete or unknown in reference data.',power_calendar:'The settlement index calendar does not cover this whole delivery period.',power_index:'This curve needs a single settlement index with matching commodity, currency and unit.',tou_definition:'Supply a lowercase code, a name, a market clock and an ordered peak window.',tou_conflict:'This TOU product changed or its code is already used. Reload before editing.',clock_reference:'Choose a market clock from this workspace’s reference data.',calendar_reference:'Choose a business calendar from this workspace’s reference data.',calendar_binding:'Choose a reference calendar, not a typed one.',futures_revision_conflict:'This trade changed. Reload the current revision before amending.',futures_amendment:'Supply an explicit amendment reason and use the selected working As of time.',futures_no_change:'Change a supported booking term before previewing an amendment.',futures_trade:'The trade revision is unavailable in this context.',historical_read_only:'This historical view is read-only. Return to working data before booking or amending.',risk_stress:'Choose signed nonzero curve-coordinate shifts using the supported additive convention, with at most sixteen nested stresses.',scenario_target:'Choose an available exact curve, contract or point target; overlapping shifts belong in separate nested layers.',scenario_coordinate:'Choose a primitive quote for input shocks or a computed point for output shifts. Frozen observations cannot be shocked.',scenario_bucket:'Choose an ordered delivery window whose boundaries do not split a contract or computed point.',scenario_shift:'Choose an offered quote or output target, its exact unit and a valid signed shift within the supported nesting limit.',risk_context:'Select the shared financial context before running risk.',risk_job_context:'Queue the exact retained financial context with a stable request identity.',risk_job_conflict:'This request identity belongs to different risk inputs. Inspect the retained job before submitting a new request.',risk_job_run:'The parts of this run do not agree: every part of a run is queued once, at one retained context, with one part count. Queue the run again.',risk_job_outcome_unknown:'The job submission outcome is unknown. Reconcile the retained submission to retry the same request safely.',risk_engine_version:'This retained job requires its original managed engine version.',risk_timeout:'The risk job exceeded its configured calculation time limit.',risk_input_timeout:'The retained risk inputs could not be loaded within the configured time limit.',risk_result_limit:'The risk result exceeds the retained report size limit. Narrow the selected books or request book totals.',risk_bump:'Enter a positive absolute bump and choose distinct supported scenario targets.',risk_factor:'Choose an available quote or computed-output target in this market context.',risk_position:'Choose books containing live supported holdings.',risk_policy:'Choose the supported exact repricing policy.',risk_convention:'Choose the supported repricing convention for this risk operation.',portfolio_policy:'Refresh the risk catalog to select its complete supported pricing policies.',portfolio_context:'Select a complete retained financial context covering the held instruments and their market inputs.',swap_risk_model:'A held swap requires market inputs that are unavailable in this exact financial context.',precision_loss:'An exact result needs more precision. For a conversion, choose explicit final rounding; no value was rounded automatically.',price_domain:'A base or shocked price is outside the contract’s allowed price range.',table_view_conflict:'This saved view changed in another session. Use Reload views before saving or deleting it.',table_view:'Choose a valid named layout; at most twenty views can be saved per table.',blotter_view:'Choose available books and explicit pivot dimensions/measures. Lots require dated-contract grouping.',power_historical:'Return to current inputs to use the unsaved power example. Historical clock publication is not supported yet.',power_units:'This example needs an outright curve quoted per MWh.',settlement_coverage:'The selected mask has no intervals or the fixing coverage is incomplete.',power_input:'Complete the product, units and classification choices.',access_denied:hostedPilot?'Your account does not have permission for that operation.':'This demo user can’t make that change. You can switch users in Profile → Preview access.',revision_conflict:'This price has already changed. Refresh and choose the version you want in Price changes.',mutation_outcome_unknown:'We could not confirm whether your change was saved. Refresh and check the saved changes before saving again.',numeric_format:'Enter a number with no commas or spaces, using up to 18 decimal places.',missing_value:'Enter a new value before saving.',definition_rejected:'This value does not meet the contract’s price rules. Check the allowed range and settlement increment in Reference data.',snapshot_missing:'Choose an available market date.',request_token:'Please refresh the page before making this change.',request_cancelled:'This took too long. Please refresh and try again.',admin_conflict:'This account changed or the login name is already registered. Refresh accounts before editing.',last_administrator:'Keep at least one current administrator in this workspace.',self_access_change:'Another administrator must remove your administrative access.',admin_input:'Enter a name, login, identity kind and explicit roles.',admin_limit:'This preview has reached its account limit.',administration_outcome_unknown:'We could not confirm the change. Check accounts and recent access changes, then retry the retained request if necessary.',model_admin_input:entryRanges,model_admin_conflict:'This provider or model changed, the model already exists, its current state does not allow the change, or the request identity was used for a different change. Refresh AI settings before trying again.',model_admin_outcome_unknown:'We could not confirm the change. Check the current AI settings and recent changes, then retry the retained request if necessary.',model_identifier_invalid:'Enter the vendor’s model identifier: up to 128 letters, digits, dots, underscores or hyphens, starting with a letter or digit.',currency_unavailable:'Choose a currency of a sealed reference revision that your roles can currently read.',model_unqualified:'Enable a model once its capabilities are recorded under the usage mapping its vendor’s adapter applies.',model_provider_unqualified:'The provider is not qualified: this release must confirm its usage mapping, and its current key and terms must be attested.',model_provider_disabled:'The model’s provider is disabled. Enable the provider first.',model_mapping_unconfirmed:'Record capabilities once this release confirms a usage mapping for the model’s vendor.',model_unpriced:'The model needs a rate set in force now.',model_changed:'The model’s capability version changed. Refresh AI settings and choose the version listed now.',model_disabled:'The model is disabled. Enable it before an agent version names it.',model_limit_exceeded:'Ask for no more output tokens per call than the model version allows.',grant_duration_exceeded:'The grant is longer than the limit stated with it.',instructions_too_long:'The instructions are longer than the limit stated with them.',agent_retired:'This agent is retired and can no longer change.',parameter_required:'Choose one of the reasoning efforts the model’s capability version records.',test_in_progress:'This test has no recorded outcome yet. Retry the retained request to read it once it is recorded.',report_zone_invalid:'Choose a time zone the preview’s database knows, named exactly as it names it.',report_range_invalid:'Enter a From date before the To date, each written yyyy-MM-dd, no further apart than the Usage tab states.',agent_runtime_unavailable:'Tests run only while this process holds the agent worker, which another process may hold or whose connection was lost. Try again shortly.',credential_unavailable:'Key storage is unavailable here, so no key can be saved or opened. Its key files are missing or unreadable: ask whoever runs this installation to restore them.',agent_managed:'Agents can’t be created or changed here, and only agents hold the agent reader role.',numeric_range:'A number is too large: its magnitude must be below 10^24.',interval_series_resolution:'Choose a 5, 15, 30 or 60 minute resolution.',interval_series_empty:'Add at least one value to import.',interval_series_size:'A revision holds at most 200,000 intervals, so split a larger import into several smaller ones.',interval_series_instant:'Write each start as a UTC instant, for example 2027-01-01T05:00:00Z, within the years 2000 to 2099.',interval_series_grid:'Every start must be where one of the series’ intervals starts on its market clock, and From and To may not fall inside a stored interval.',interval_series_overlap:'Intervals within one import cannot overlap or repeat.',interval_series_historical:'Return to working data to edit or read interval series, then use Known at on this page to see earlier knowledge.',interval_curve_historical:'Return to working data to read interval curves; their TOU and shape inputs are current definitions.',interval_curve_window:'Choose From and To on the curve’s interval grid, at most 35,136 intervals apart.',interval_curve_cutoff:'Choose Known at for a shaped curve or when resolving actuals.',interval_contract:'Choose a curve whose constituents are exact delivery intervals on its market clock.',interval_block:'A block curve must be date-only and bound to a published TOU peak window, or cover all hours.',interval_block_partition:'The curve’s block marks must cover every interval in this window exactly once. Choose a window inside its marked months.',interval_block_coverage:'No block of the curve covers part of this window. Choose a window inside its marked months.',interval_shape:'The shape cannot calibrate a block: multiplicative factors need a positive weighted mean under the supported method.',interval_shape_coverage:'The shape has no value for an interval of a block this window touches at the chosen Known at. Choose a later Known at or complete the shape.',interval_actual:'This curve has no collected actual-price source and due rule, or its collected prices do not match its intervals.',interval_actual_missing:'A delivered interval’s actual price is due but was not collected by the chosen Known at.',interval_aggregate:'Aggregate resolved prices over whole local hours or operating days, with From and To on their boundaries.',interval_curve_coverage:'The curve has no interval for part of this window. Choose From and To within its intervals.',interval_delivered:'This window includes intervals delivered by the market date. Choose Resolve to price them from collected actuals.',interval_series_conflict:'This request was already used for a different change, so start a new request.',interval_series_revision_conflict:'Someone else saved this series first, so reload it and apply your change again.',interval_series_values:'Check the values: each must be a plain decimal with at most 18 decimal places inside the series’ allowed range, and no interval may overlap a stored one that starts at a different time.',interval_series_window:'Choose a From before To spanning at most 35,136 intervals and 100,000 changes, with a Known at no later than now.',interval_series_code:'Choose an unused code that starts with a lowercase letter and uses lowercase letters, digits, underscores, dots or hyphens.',interval_series_definition:'Name the series in 1 to 80 characters without leading or trailing spaces, and choose a kind, specification and clock that belong together in this workspace’s reference data.',interval_series_request:'This request has no identity, so refresh the page and try again.',interval_series_reason:'Give a reason of 1 to 500 characters that is not blank.',interval_series_outcome_unknown:'We could not confirm whether the save completed, so retry the original request to recover its receipt or check the change log first.',request_too_large:'The request is too large for this operation; for an import, split it into smaller ones.',overlay_parameter:'Choose a known overlay with a whole-number window from 2 to 250, and at most four overlays.',overlay_input:'The chart overlays need an ordered price history of at most 35,136 points with plain decimal values.',history_conflict:'Different prices remain at one instant under the cutoff policy. Choose Exact closes or one version to read this history.',power_period_interval:'A stored delivery price is not an interval of these operating days under the source’s market clock, so the read was refused.',power_period:'Choose operating dates written yyyy-MM-dd that span 1 to 366 days within 2000 to 2099, and a day inside the contract’s delivery period when settling.',custom_curve_mixed_units:'The preview mixes output units, so no single unit describes it. Give the outputs one unit.'};throw Object.assign(Error(agentRefusal(error)??scheduleErrorMessage(error.code)??messages[error.code]??requestRefusal(error)),{code:error.code});}
  return response.json() as Promise<T>;
}
async function api<T>(path:string,body?:unknown,signal?:AbortSignal):Promise<T>{
 const payload=await transport<T>(path,body,apiRoot,signal);
 if(['profile','market/current','market/context'].includes(path))stopLive();
 if(/^(trading\/(blotter|workspace|position)|swaps\/(preview|editor)|futures\/(holdings|editor|amend-preview)|evaluate|history|power\/preview)$/.test(path)){
  // A workspace read's report and its desk summaries are both shown; its catalog is not.
  const displayed=path==='trading/workspace'?{blotter:(payload as {blotter:unknown}).blotter,summaries:(payload as {summaries?:unknown}).summaries}:path==='evaluate'||path==='history'?{points:(payload as CurveResult|ContractHistory).points}:payload;
  await prepareAmountDisplay(displayed,request=>transport<string[]>('amounts/display',request,apiRoot,signal),path==='history'?decimalTick():undefined);
 }
 return payload;
}
function pageStatus(){return page==='delivery'?'Delivery prices · selected delivery dates and knowledge cutoff.':page==='ppa'?'PPA bookings · exact selected inputs and certified knowledge.':page==='interval-series'?'Interval data · working data, known at the chosen time.':page==='interval-curves'?'Hourly / sub-hourly prices · marks from the selected market date; shapes and actuals known at the chosen time.':page==='custom-curves'?'Curve builder · selected market context.':page==='jobs'?'Jobs and infrastructure · current authority.':page==='futures'?'Trading blotter · saved market inputs.':page==='admin'?'Workspace identity and access administration.':page==='ai'?'Page-assistant settings · current authority.':page==='schedules'?'Reusable schedule shortcuts.':result?`Prices for ${instant(result.at)}`:select('snapshot').value?'Choose a curve to get started.':'Choose a curve and a market date to get started.';}
const errorMessage=(error:unknown)=>error instanceof Error?error.message:'Something went wrong. Please refresh the page.';
// `keepNotice` is for work that completes a change the person has already made, as reading a changed effective date's end-of-day
// knowledge does: what that change said, such as that it cleared a risk run, stays on screen.
async function act(work:()=>Promise<void>,{keepNotice=false}={}){
  // Context changes, writes and reads are serialized. Never retry a mutation.
  // Errors and notices describe the previous action; the next one starts clean.
  if(busy)return;busy=true;syncControls();$('error').hidden=true;if(!keepNotice)$('notice').hidden=true;text('status','Loading…');
  try{await work();text('status',pageStatus());}
  catch(error){text('error',errorMessage(error));$('error').hidden=false;text('status','Please check the message above.');if(page==='custom-curves')await customCurves.recover().catch(()=>undefined);}
  finally{busy=false;syncControls();if(closeCheckDue){closeCheckDue=false;void recheckNewestClose();}}
}
// A move of the person's that would give up the amendment the Blotter's panel holds, or the edits of a custom curve, as another page, another
// context or another close does, asks first, in the panel, whether to; and it asks before the page is busy, because every control, the question's own included,
// is disabled while it is. `modal` is a dialog open over the page, which keeps the panel out of reach, and is closed for the question.
// false means the person kept the amendment.
// `move` says what the move does to a custom curve's edits: 'discards' them (another page, context or account), changes the close ('closes'), which
// keeps the graph and gives up only an unpublished effective-version timeline, or 'keeps' them (preferences, following live). The Blotter's amendment
// is asked about for every move, and always through its own leave, which also closes its forms.
type Move='discards'|'closes'|'keeps';
async function mayLeave(move:Move='discards',modal?:HTMLDialogElement){
  if(page==='futures'){
    if(modal?.open&&futures.editing())modal.close();
    return futures.leave();
  }
  if(page!=='custom-curves'||move==='keeps')return true;
  const scope=move==='closes'?'timeline':'edits';
  if(!customCurves.holds(scope))return true;
  if(modal?.open)modal.close();
  if(!await customCurves.leave(scope))return false;
  if(move==='discards')customCurves.discard();
  return true;
}
// Whether the page showing holds edits that a move to another page, context or account gives up: the Blotter's amendment, or a custom curve's unsaved edits.
function holdsEdits(){return page==='futures'?futures.editing():page==='custom-curves'&&customCurves.holds('edits');}
// What the profile dialog does gives up what the Blotter holds, which is asked about with the dialog closed, for a modal dialog keeps the panel, and
// the question, out of reach. Keeping the amendment leaves the dialog closed and nothing done, which is said, for a closed dialog looks like a done one.
async function mayLeaveForDialog(notDone:string,move:Move='discards'){
  if(busy)return false;
  if(await mayLeave(move,$<HTMLDialogElement>('profile-dialog')))return true;
  notice(page==='futures'?`${notDone}, because you kept the amendment you were editing.`:`${notDone} · your curve edits are unchanged.`);
  return false;
}
// Runs `work` as an action, once the person has agreed to leave what the page holds. A discarded curve that the work then failed to leave is read again.
function leaving(work:()=>Promise<void>,move:Move='discards'){void (async()=>{if(busy)return;if(await mayLeave(move))await act(work);})();}
function action(name:string,run:()=>Promise<void>){const button=el('button',name);button.disabled=busy;button.type='button';button.onclick=()=>void act(run);return button;}
function decimalTick(){if(!workspace||!curve||listedCurve().marketType!=='futures')return undefined;return val(row('quote_specifications','code',val(selectedCurve(),'quote_code'),true),'clearing_increment');}
function table(target:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]=[],displayRows?:string[][],onRowActivate?:(index:number)=>void){
  const referenceScope=[data().profile,page,referenceTable,referencePage?.referenceRevision,referencePage?.offset,referencePage?.search,historicalContext()];
  const marketScope=[data().profile,curve,result?.snapshotId??'',target==='history-values'?contract:'',target==='revisions'?input:'',...(['inputs','mappings'].includes(target)?[showAllContracts||target==='inputs'&&sourceOnlyCurve?'all':pricingContract]:[])];
  renderRegularTable($(target),target,JSON.stringify(target==='reference'?referenceScope:marketScope),headers,rows,numeric,()=>busy,{displayRows,onRowActivate});
}

function clearPrices(){result=undefined;pricingContract='';closeContract();input='';draftFor='';$('revision-card').hidden=true;text('build-info','');text('price-date','Choose a market date to view prices.');if(curve)renderSummary();clearChart($('curve-chart'),'Choose a market date above.');$('curve-chart').classList.add('empty');$('contracts').replaceChildren();$('inputs').replaceChildren();if(curve)renderDependencies();}
const showsDate=(next:string)=>!['admin','ai','jobs','interval-series','ppa','delivery'].includes(next);
const showsContext=(next:string)=>showsDate(next)&&next!=='interval-curves';
async function setPage(next:string){
  if(navigation?.live){
    if(['market','custom-curves','futures','reference'].includes(next)){
      connectLive(navigation.live);
      if(next==='market'&&next!==page&&lastLiveUpdate)liveUpdates?.offer({...lastLiveUpdate,generation:lastLiveUpdate.live.generation},true);
    }else{
      const pinned=await api<{frame:{id:string};context:Context}>('market/live/pause',{selection:navigation.live.id,snapshotId:frame().id});
      stopLive();clearPrices();await reload(pinned.frame.id);dateEditor.showContext(pinned.context);
      $('live-status').hidden=false;$('live-status').dataset.state='updating';text('live-status','Paused · this screen retains the displayed context');
    }
  }
  invalidateMarketDates();
  // The action that opens a page showing As of then asks whether a newer close has arrived, after the page's own reads.
  closeCheckDue=showsDate(next);
  $('curve-price-navigation').hidden=!['market','interval-curves'].includes(next);
  $('curve-price-contracts').setAttribute('aria-pressed',String(next==='market'));
  $('nav-interval-curves').setAttribute('aria-pressed',String(next==='interval-curves'));
  // The Blotter is left on the person's word, whichever route took them, so no form of its panel stays held, with edits that the browser would be asked
  // about and that nothing can return to: the Blotter is read afresh when they come back to it.
  if(page==='futures'&&next!=='futures')futures.leavePage();
  // Only the reference page has an address; another page leaves none behind.
  if(next!=='reference'&&location.hash.startsWith('#reference'))window.history.replaceState(null,'',location.pathname+location.search);
  page=next;$('ppa-page').hidden=next!=='ppa';$('custom-curves-page').hidden=next!=='custom-curves';$('jobs-page').hidden=next!=='jobs';$('delivery-page').hidden=next!=='delivery';$('interval-series-page').hidden=next!=='interval-series';$('interval-curves-page').hidden=next!=='interval-curves';$('parties-page').hidden=next!=='parties';$('futures-page').hidden=next!=='futures';$('power-page').hidden=next!=='power';select('snapshot').closest<HTMLElement>('.date-control')!.hidden=!showsDate(next);$('market-page').hidden=next!=='market';$('reference-page').hidden=next!=='reference';$('schedules-page').hidden=next!=='schedules';$('curve-date-editor').hidden=next!=='configuration';$('date-context').hidden=!showsContext(next);$('admin-page').hidden=next!=='admin';$('ai-page').hidden=next!=='ai';$('no-access-page').hidden=true;
  text('page-section',pageGroup(next).toUpperCase());text('header-calculation-time','');text('page-title',next==='jobs'?'Jobs & Infrastructure':next==='custom-curves'?'Curve builder':next==='delivery'?'Delivery prices':next==='interval-series'?'Interval data':next==='interval-curves'?'Curves & prices':next==='parties'?'Entities & counterparties':next==='ppa'?'PPA bookings':next==='futures'?'Blotter':next==='power'?'Power TOUs':next==='admin'?'Users & access':next==='ai'?'AI':next==='market'?'Curves & prices':next==='configuration'?'Contract definitions':next==='schedules'?'Schedules':'Reference data');
  for(const name of ['market','custom-curves','delivery','interval-series','interval-curves','power','reference','configuration','schedules','admin','ai','parties','jobs'])$('nav-'+name).classList.toggle('active',next===name||(name==='market'&&next==='interval-curves'));
  renderBlotterViews();
  text('status',pageStatus());syncControls();
}
function showView(view:Preferences['tab']){document.querySelectorAll<HTMLElement>('.view').forEach(n=>n.hidden=n.id!==`view-${view}`);document.querySelectorAll<HTMLButtonElement>('.tab').forEach(b=>{const active=b.dataset.view===view;b.classList.toggle('active',active);b.setAttribute('aria-selected',String(active));});}
function renderLayout(){
  document.body.classList.toggle('sidebar-collapsed',prefs.sidebarCollapsed);document.body.classList.toggle('library-collapsed',prefs.libraryCollapsed);
  const sidebar=$('sidebar-toggle');sidebar.setAttribute('aria-expanded',String(!prefs.sidebarCollapsed));sidebar.setAttribute('aria-label',prefs.sidebarCollapsed?'Expand navigation':'Collapse navigation');sidebar.title=prefs.sidebarCollapsed?'Expand navigation':'Collapse navigation';sidebar.textContent=prefs.sidebarCollapsed?'›':'‹';
  for(const {id} of sidebarGroups){const group=groupNode(id);group.open=prefs.sidebarCollapsed||!prefs.collapsedMenus.includes(id);group.querySelector('summary')!.hidden=prefs.sidebarCollapsed;}
  const library=$('library-toggle');library.setAttribute('aria-expanded',String(!prefs.libraryCollapsed));library.setAttribute('aria-label',prefs.libraryCollapsed?'Expand curve list':'Collapse curve list');library.title=prefs.libraryCollapsed?'Expand curve list':'Collapse curve list';$('curves').hidden=prefs.libraryCollapsed;
}
function renderTimeZones(){document.querySelectorAll<HTMLElement>('[data-user-time-zone]').forEach(n=>{n.textContent=prefs.timeZone;});}
function invalidateMarketDates(){
 marketDateGeneration++;marketDatesLoaded=false;
 marketCalendar.invalidate(contextDirty?'Apply the changed history and knowledge context first.':'Open the calendar to check available closes.');
}
type MarketDatesRequest={curve:string|null;timeZone:string;definition:unknown};
const datesMismatch=()=>Error('The available market dates do not match this workspace. Reload the calendar.');
// What the calendar offers for one scope, checked as describing this workspace. It resolves to nothing when the
// workspace or the scope changed while it loaded, since that answer no longer applies.
async function readAvailability(request:MarketDatesRequest):Promise<MarketDates|undefined>{
 const owner=directory(),generation=marketDateGeneration,stale=()=>navigation!==owner||generation!==marketDateGeneration;
 let catalog:MarketDates;
 try{catalog=await api<MarketDates>('market/dates',request);}catch(error){if(stale())return undefined;throw error;}
 if(stale())return undefined;
 const frames=catalog.dates.flatMap(group=>group.frames);
 // Every close's time must be readable, as the newest close and the badge compare them, before anything keeps the answer.
 if(catalog.selectionPolicy!=='exact_snapshot_no_close_substitution_v1'||catalog.timeZone!==prefs.timeZone||catalog.curve!==request.curve||
  JSON.stringify(catalog.context??null)!==JSON.stringify(historicalContext()??null)||new Set(frames.map(close=>close.id)).size!==frames.length||
  catalog.dates.some(group=>!/^\d{4}-\d{2}-\d{2}$/.test(group.date)||!group.frames.length)||frames.some(close=>Number.isNaN(Date.parse(close.at))))
  throw datesMismatch();
 newestDate(catalog); // refuses a newest date its dates do not support
 return catalog;
}
// The same read for a page that selects from it, so every close must be one the page offers. A catalog ahead of the
// page's saved closes loads them once and is kept only while the selected close is still offered.
async function readMarketDates(request:MarketDatesRequest,refresh=true):Promise<MarketDates|undefined>{
 const catalog=await readAvailability(request);
 if(!catalog)return undefined;
 const owner=directory(),allowed=new Set([...select('snapshot').options].map(option=>option.value)),frames=catalog.dates.flatMap(group=>group.frames);
 if(frames.every(close=>allowed.has(close.id)&&owner.frames.some(frame=>frame.id===close.id&&frame.at===close.at)))return catalog;
 if(refresh&&catalog.frameCatalog?.durableSelection==='exact_recorded_close_pins_v1'&&frames.some(close=>!allowed.has(close.id))){
  const selected=select('snapshot').value;await reload(selected);
  if(selected&&select('snapshot').value!==selected){clearPrices();throw Error('The selected close is outside the current saved-close catalog. Choose an available close explicitly.');}
  return readMarketDates(request,false);
 }
 throw datesMismatch();
}
async function openMarketCalendar(date?:string):Promise<void>{
 if(!navigation||contextDirty)return;
 if(marketDatesLoaded){marketCalendar.show(date);return;}
 const definition=page==='custom-curves'?customCurves.dateDefinition():undefined;
 if(page==='custom-curves'&&!definition){marketCalendar.unavailable('Complete a curve graph or apply your formula to see available closes.');marketCalendar.show();return;}
 const request={curve:(page==='market'||page==='power')&&curve?curve:null,timeZone:prefs.timeZone,definition:definition??null};
 marketCalendar.loading();
 try{
  const catalog=await readMarketDates(request);
  if(!catalog)return;
  // Read for the whole workspace of working data, the calendar is as new as a check for a newer close, and the badge says what it found.
  if(request.curve===null&&request.definition===null&&!historicalContext())workspaceCloses=catalog;
  marketDatesLoaded=true;marketCalendar.update(catalog);marketCalendar.show(date);
 }catch(error){
  if((error as {code?:string}).code==='access_denied'){clearPrices();if(page==='custom-curves')customCurves.contextChanged();}
  marketCalendar.unavailable(page==='custom-curves'?'Complete a valid curve graph with available inputs to see its closes.':'Market-date availability could not be verified. Open the calendar to retry.');
  if(page==='custom-curves'&&['recipe_definition','custom_curve_definition','custom_curve_source','recipe_formula'].includes((error as {code?:string}).code??'')){marketCalendar.show();return;}
  throw error;
 }
}
// Opening working data on its newest close (D219, with D217's latest instant). The workspace-wide calendar's uniquely latest
// close is selected as if the person had picked it, several closes at that instant ask, and none selects nothing. The page
// that is showing then reads it.
const workspaceDates=():MarketDatesRequest=>({curve:null,timeZone:prefs.timeZone,definition:null});
async function selectNewestClose():Promise<CloseChoice>{
 const catalog=await readMarketDates(workspaceDates());
 if(!catalog)return {kind:'none'};
 const choice=newestClose(catalog);
 workspaceCloses=catalog;
 if(choice.kind==='single'){select('snapshot').value=choice.closeId;closeChanged();}
 return choice;
}
// Several closes at the workspace's latest instant ask which (D162): the question is put on the workspace calendar it comes from, which
// offers them whatever the page's own calendar covers. The page's calendar is read again when it is next opened.
function askNewestClose(date:string){
 if(!workspaceCloses)return;
 marketDatesLoaded=false;marketCalendar.update(workspaceCloses);marketCalendar.show(date);
}
// A click on Newer close available. Selecting it loads the page like a close the person picked.
async function openNewestClose(){
 const choice=await selectNewestClose();
 if(choice.kind==='single')await loadSelectedClose();
 else if(choice.kind==='choose')askNewestClose(choice.date);
}
// A newer close can arrive while a session is open. Opening a page and returning to the window ask again, in the
// background and without polling. The badge reports what the check found and only a click on it selects the newer close.
// A check that could not be made leaves the badge as the last one left it, and the next page or focus asks again.
async function recheckNewestClose(){
 if(checkingCloses||!navigation||contextDirty||historicalContext()||!showsDate(page))return;
 const owner=navigation;checkingCloses=true;
 try{
  const catalog=await readAvailability(workspaceDates());
  if(catalog&&navigation===owner){workspaceCloses=catalog;renderCloseBadge();}
 }catch{/* the badge stays as the last check left it */}
 finally{checkingCloses=false;}
}
// The badge compares the close shown with the workspace's newest. Its basis is the latest instant known when that close was
// selected (taken the first time the close is rendered, and again whenever it is selected), so a later close is news
// whoever selected the close. A historical or unapplied view has no newest close to claim, and a workspace without closes
// has nothing to compare.
function renderCloseBadge(){
 const closes=workspaceCloses,shown=shownClose,latest=closes?latestInstant(closes):null;
 if(latest&&closeBasis?.close!==shown)closeBasis={close:shown,latest};
 marketCalendar.badge(closes&&latest&&closeBasis&&!contextDirty&&!historicalContext()?closeBadge(shown,closeBasis.latest,closes):null);
}
// The As of dates the selector offers: a historical view's own date's frames, or every frame of working data.
function offeredFrames(){const context=historicalContext();return context?directory().frames.filter(f=>f.at.slice(0,10)===context.effectiveOn):directory().frames;}
function renderFrames(value:string){const frames=offeredFrames();choose('snapshot',[{value:'',name:frames.length?'Choose a date…':'No inputs for this context date'},...frameOptions(frames,prefs.timeZone)],value);select('snapshot').title=`Times shown in ${prefs.timeZone}`;shownClose=select('snapshot').value;invalidateMarketDates();}
// The board's shapes are those of one close of one directory, read for one account and context: any change of them leaves none read.
const boardScope=()=>JSON.stringify([administrationScope,currentProfile,select('snapshot').value,contextDirty]);
function readShapes():ReadonlyMap<string,CurveShape|string>{const kept=boardShapes;return kept&&kept.owner===navigation&&kept.scope===boardScope()?kept.shapes:new Map();}
const board=()=>marketBoard(directory(),{by:prefs.browseBy,commodity:prefs.market,type:prefs.marketType},readShapes());
// Choosing a curve, from the library or from its card on the board, or no curve, which shows the board again.
async function selectCurve(code:string){
  curve=code;invalidateMarketDates();clearPrices();
  if(!curve){renderContext();await loadBoard();}
  else if(select('snapshot').value)await build();else{await loadWorkspace();renderContext();}
}
function chooseCurve(code:string){void act(()=>selectCurve(code));}
// The market board, while no curve is shown: every listed curve at the selected close, or what to choose before there is one.
function renderMarketBoard(){
  const shown=!curve||!workspace,close=navigation&&!contextDirty?navigation.frames.find(f=>f.id===select('snapshot').value):undefined;
  $('market-board').hidden=!shown;$('market-board-empty').hidden=!!close;$('market-board-groups').hidden=!close;
  text('market-board-status',close?`Prices at the close of ${instant(close.at)}`:'');
  text('market-board-empty-title',contextDirty?'Apply the changed context':'Choose a market date');
  text('market-board-empty-help',contextDirty?'View the changed context, then choose a close in As of to see every curve at it.':'Pick a close in As of above to see every curve at that close, or choose a curve from the list.');
  if(shown&&close)renderBoard($('market-board-groups'),board(),chooseCurve,busy);else $('market-board-groups').replaceChildren();
}
// Reads the curve shapes of the listed curves at the selected close that have none the backend stated, at most 64 to a read, and draws
// them on the board and in the library. A curve whose read failed shows why, and is read again by the next load. A read that a change of account, directory, close or context overtakes is refused, as build() refuses one.
async function loadBoard(){
  if(page!=='market'||contextDirty||!navigation||!select('snapshot').value)return;
  const owner=directory(),started=marketGeneration,scope=boardScope(),known=readShapes(),reads=shapeReads(board());
  if(reads.length){
    const source={snapshotId:frame().id,context:historicalContext()};
    const read=await Promise.all(reads.map(curves=>readCurveShapes(api,source,curves)));
    if(navigation!==owner||started!==marketGeneration||scope!==boardScope())throw Error('The account or market selection changed. Reload before continuing.');
    const shapes=new Map(known);
    reads.forEach((curves,i)=>{for(const code of curves)shapes.set(code,read[i].get(code)??'Curve shape unavailable · the read did not return this curve');});
    boardShapes={owner,scope,shapes};
  }
  renderBrowse();renderMarketBoard();
}
function renderBrowse(){
  const categories=navigationCategories(directory()),commodities=categories.map(c=>c.code);
  // No commodity code is empty, so the empty value can only mean All markets.
  choose('commodity',[{value:'',name:'All markets'},...categories.map(c=>({value:c.code,name:c.name}))],prefs.market??'');
  // Discard an unavailable presentation filter; market inputs remain separately pinned.
  if(prefs.market!==null&&!commodities.includes(prefs.market)){prefs.market=null;select('commodity').value='';}
  select('browse-by').value=prefs.browseBy;document.querySelectorAll<HTMLButtonElement>('[data-type]').forEach(b=>{const active=b.dataset.type===prefs.marketType;b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));});
  const groups=board();text('curve-count',String(groups.reduce((n,g)=>n+g.cards.length,0)));
  const owner=directory();
  $('curves').replaceChildren(...groups.map(group=>{const section=el('details'),key=prefs.browseBy+'|'+group.key;section.className='curve-group';section.open=!prefs.collapsedCurveGroups.includes(key);section.append(el('summary',group.name));
    section.ontoggle=()=>{if(!section.isConnected||navigation!==owner)return;const closed=prefs.collapsedCurveGroups.includes(key);if(closed===!section.open)return;prefs.collapsedCurveGroups=section.open?prefs.collapsedCurveGroups.filter(k=>k!==key):[...prefs.collapsedCurveGroups,key].slice(-64);persist();};
    // The kind badge, name and type are the row's text; the commodity dot and the curve's sparkline beside them are decorative.
    group.cards.forEach(c=>{const button=el('button'),line=librarySparkline(c);button.className='curve-button'+(c.curve===curve?' selected':'');button.disabled=busy;button.setAttribute('aria-pressed',String(c.curve===curve));button.append(commodityDot(c),kindBadge(c.curveKind),el('span',c.name),el('small',c.type),...(line?[line]:[]));button.onclick=()=>chooseCurve(c.curve);section.append(button);});return section;}));
  if(!groups.length)$('curves').append(el('p','No curves match these choices.'));
}
function renderContext(){
  const example=directory().example;$('example').hidden=!example;text('example-help',example?`${curveName(example.curve)} · ${instant(example.at)}`:'No market inputs have been imported.');renderLayout();renderBrowse();renderMarketBoard();$('curve-detail').hidden=!curve||!workspace;
  $('curve-power').hidden=!curve||!workspace?.powerCurveCodes.includes(curve);
  if(curve&&workspace){const c=selectedCurve(),listed=listedCurve(),kind=listed.marketType;renderSummary();text('curve-kind',navigationCurveKind(listed));text('curve-title',curveName(curve));$('curve-kind-badge').replaceChildren(kindBadge(listed.kind,'md'));
    const specification=kind==='rates'?row('interest_rate_definitions','code',val(c,'interest_rate_code'),true):undefined;
    text('unit',specification?`${val(row('currencies','code',val(specification,'currency_code'),true),'display_code')} · % per year`:unit(val(c,'quote_code')));
    text('chart-title',kind==='rates'?'Zero-rate curve':kind==='futures'?'Futures curve':kind==='forwards'?'Forward curve':'Mixed curve');
    text('source-title',kind==='rates'?'Rates behind this curve':'Prices behind this curve');text('source-help',kind==='rates'?'Annual zero rates for the listed maturities.':'Prices and conversion factors for the selected market date.');text('contracts-help',kind==='rates'?'Choose a maturity to see its rate history and how it’s priced.':'Choose a contract to see its price history and how it’s priced.');
    renderDependencies();if(result)renderInputs();}
  showView(prefs.tab);
}
function inputUnit(series:Row){
  if(data().definitions.rate_curve_points.some(p=>p.curve_code===curve&&p.series_id===series.id))return 'Annual rate (decimal)';
  if(series.quote_code)return unit(val(series,'quote_code'));const spec=row('value_specifications','code',val(series,'value_specification_code'),true);if(spec.kind==='dimensionless')return 'Ratio';
  const a=row('quantity_specifications','code',val(spec,'numerator_quantity_code'),true),b=row('quantity_specifications','code',val(spec,'denominator_quantity_code'),true);return `${commodityName(val(a,'commodity_code'),data())} ${val(row('units','code',val(a,'unit_code'),true),'symbol')} / ${commodityName(val(b,'commodity_code'),data())} ${val(row('units','code',val(b,'unit_code'),true),'symbol')}`;
}
function sourceName(series:Row){
  const rate=data().definitions.rate_curve_points.find(p=>p.curve_code===curve&&p.series_id===series.id);if(rate)return `Zero rate · ${displayDate(val(rate,'maturity'))}`;
  if(!series.contract_id)return 'Conversion factor';
  const direct=data().definitions.curves.filter(c=>c.rule==='direct'&&c.quote_code===series.quote_code&&data().definitions.curve_members.some(m=>m.curve_code===c.code&&m.contract_id===series.contract_id));
  return direct.length===1?curveName(val(direct[0],'code')):'Market price';
}
function settlementText(){
  const target=row('settlement_targets','code',val(selectedCurve(),'settlement_target_code'),true);
  return target.kind==='future_series'?'Follows this futures contract':`${referenceName('price_index_definitions',val(target,'index_a_code'))}${target.index_b_code?' less '+referenceName('price_index_definitions',val(target,'index_b_code')):''}`;
}
// Convention labels come with the evaluated rate result; the formula below is only drawn for that exact policy.
function rateConvention(){
  if(!result||result.numericalPolicy!=='simple_actual365fixed_rational_to_even_18_v1'||!result.convention)throw Error('This interest-rate convention cannot be displayed yet.');
  return result.convention;
}
function renderSummary(){
  const items=result&&curve?summaryTiles(result,result.kind==='rates'?'':settlementText()):[];
  $('curve-summary').hidden=!items.length;
  // A figure is one token that never splits; the curve's words, as its settlement, wrap in their tile instead.
  $('curve-summary').replaceChildren(...items.map(item=>renderTile({label:item.name,rows:[{figure:item.value}],words:!item.numeric})));
  const context=historicalContext(),banner=$('curve-mode');banner.hidden=!curve||contextDirty||(!context&&!canCorrect());
  banner.dataset.tone=context?'historical':'editor';
  text('curve-mode',context?`Historical view: ${displayDate(context.effectiveOn)}${context.knownAt?`, as known at ${instant(context.knownAt)}`:''}. Prices are read-only; return to working data to make changes.`:'You can correct source prices on the Price sources tab. Every earlier version stays available.');
}
function renderPrices(){
  if(!result)return;renderSummary();const rates=result.kind==='rates';if(result.availability==='no_remaining_contracts'){pricingContract='';closeContract();text('price-date',`As of: ${instant(result.at)}`);text('build-info','No remaining delivery days');clearChart($('curve-chart'),'No remaining delivery days for this market date.');$('contracts').replaceChildren();renderDependencies();renderInputs();return;}
  const exportCurve={snapshotId:result.snapshotId,curve:result.curve};
  renderChart($('curve-chart'),{exportImage:download=>void act(async()=>{try{await api('evaluate',exportCurve);}catch(error){clearPrices();throw error;}download();}),title:curveName(result.curve),axis:rates?'maturity':'delivery',unit:$('unit').textContent??'',series:[{name:curveName(result.curve),points:result.points.map(p=>({x:pointIdentity(p),label:rates?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!),y:p.value}))}]});
  text('price-date',`As of: ${instant(result.at)}`);text('build-info',contractCountText(result));
  // A row, chosen or pressed with Enter, opens its contract in the panel, for the result it was drawn from.
  const shown=result;
  table('contracts',rates?['Maturity','Zero rate (%)','Discount factor']:['Delivery period','Price','Expires'],result.points.map(p=>[contractLabel(p),p.value,rates?p.displayDiscountFactor!:instant(p.expiry!)]),rates?[1,2]:[1],result.points.map(p=>[contractLabel(p),amountText(p.value,'price',p.priceTick),rates?amountText(p.discountFactor!,'factor'):instant(p.expiry!)]),
    index=>void act(async()=>{if(result===shown)await openContract(pointIdentity(shown.points[index]));}));
  $('contracts').querySelector('table')?.setAttribute('aria-describedby','contracts-help');
  renderContract();renderDependencies();renderInputs();
}
async function build(){
  if(contextDirty)throw Error('Apply the changed context before viewing prices.');
  const owner=directory(),previousWorkspace=workspace,includeWorkspace=!workspace,started=marketGeneration,scope=marketSelection();
  const f=frame();if(!curve)throw Error('Choose a curve first.');
  const {workspace:includedWorkspace,...next}=await api<CurveResult & {workspace?:Workspace}>('evaluate',{snapshotId:f.id,curve,includeSources:true,...(includeWorkspace?{includeWorkspace:true}:{})});
  if(navigation!==owner||workspace!==previousWorkspace||started!==marketGeneration||scope!==marketSelection())throw Error('The account or market selection changed. Reload before continuing.');
  if(next.snapshotId!==f.id||next.curve!==curve||next.observationScope!=='selected_frame'||!Array.isArray(next.observations))throw Error('The selected price sources are missing or inconsistent. Refresh and try again.');
  if(includeWorkspace!==!!includedWorkspace)throw Error('The requested market catalog is missing or inconsistent. Refresh and try again.');
  const hydrated=includeWorkspace?validateWorkspace(includedWorkspace!,owner,started,scope):previousWorkspace!;
  try{workspace=hydrated;data().observations=next.observations;result=next;renderContext();renderPrices();}
  catch(error){clearPrices();if(includeWorkspace)workspace=undefined;throw error;}
}
const contractLabel=(p:Point)=>result?.kind==='rates'?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!);
const staleSelection=()=>Error('The account or market selection changed. Reload before continuing.');
// Closes the contract panel without asking, and forgets the contract it showed and its history choices.
function closeContract(){contractPanel.reset();forgetContract();}
// The contract's row is marked open in every drawing of the table, as the panel is open on it.
function openRow(){const index=result?.points.findIndex(p=>pointIdentity(p)===contract)??-1;return index<0?undefined:$('contracts').querySelectorAll<HTMLElement>('tbody tr')[index];}
function markOpenRow(){$('contracts').querySelectorAll('tbody tr[aria-current]').forEach(row=>row.removeAttribute('aria-current'));if(contract)openRow()?.setAttribute('aria-current','true');}
function forgetContract(){contract='';historyData=undefined;historySeries=[];historyFailed=false;historyPolicy=revisionPolicies[0].policy;overlayRequests=[];}
// Opens a contract of the curve on screen in the panel and reads its history. The contract the panel shows already is only given focus,
// unless its history could not be read, which is then read again.
async function openContract(identity:string){
  if(!result)return;
  if(!result.points.some(p=>pointIdentity(p)===identity))throw Error('Please select this contract again.');
  const opener=contractPanel.current()===null?document.activeElement:undefined;
  if(!await contractPanel.open({kind:'curve-contract',curve:result.curve,contract:identity})||contract===identity&&!historyFailed)return;
  if(opener instanceof HTMLElement)contractOpener=opener;
  contract=identity;historyData=undefined;historySeries=[];historyFailed=false;
  renderContract();await history();
}
// Draws the panel's contract from the result on screen: its head, how it is priced and its history. A result that no longer prices
// it closes the panel, and says so.
function renderContract(){
  if(!result||!contract)return;
  const p=result.points.find(p=>pointIdentity(p)===contract);
  if(!p){closeContract();notice('The contract the panel showed has no price at this close, so the panel was closed.');return;}
  const rates=result.kind==='rates';
  contractPanel.head({kicker:`${curveName(result.curve)} · ${rates?'Maturity':'Contract'}`,title:contractLabel(p),sub:rates?`Discount factor ${p.displayDiscountFactor}`:`Expires ${instant(p.expiry!)}`,figure:rates?p.value:amountText(p.value,'price',p.priceTick),unit:$('unit').textContent??''});
  const pricing=el('section');pricing.className='contract-flow pricing-flow';
  if(rates)pricing.append(el('h4',contractLabel(p)),rateFlow(p));else drawPricingGraph(pricing,pricingGraph(result,p));
  contractPanel.pricing().replaceChildren(pricing);
  markOpenRow();renderHistory();
}
function renderHistory(){
  if(!result||!contract)return;
  const choices={policy:historyPolicy,overlays:overlayRequests.length},rates=result.kind==='rates';
  if(!historyData){contractPanel.history({state:historyFailed?'failed':'reading',...choices});$('history-values').replaceChildren();return;}
  const p=result.points.find(p=>pointIdentity(p)===contract)!,points=historyData.points,labels=timeLabels(points.map(p=>p.at),prefs.timeZone);
  contractPanel.history({state:'read',...choices,basis:historyBasis(historyPolicy,historyData,instant),chart:{title:contractLabel(p),unit:$('unit').textContent??'',zone:prefs.timeZone,series:historyChart(historyData),overlays:historySeries}});
  table('history-values',['Market date',rates?'Zero rate (%)':'Price','Known at'],points.map((p,i)=>[labels[i],historyPriceLabel(p),knownAtText(p,instant)]),[1],
    points.map((p,i)=>[labels[i],p.value===null?historyPriceLabel(p):amountText(p.value,'price',decimalTick()),knownAtText(p,instant)]));
}
// The overlay catalog, read once for the account on screen.
async function readOverlayCatalog(){
  const scope=administrationScope+':'+currentProfile;
  if(overlayCatalog?.scope===scope)return overlayCatalog.catalog;
  const started=marketGeneration,catalog=await api<OverlayCatalog>('analytics/overlays');
  if(started!==marketGeneration||scope!==administrationScope+':'+currentProfile)throw staleSelection();
  if(typeof catalog?.policy!=='string'||!Array.isArray(catalog.overlays))throw Error('The overlay catalog is missing or inconsistent. Refresh and try again.');
  overlayCatalog={scope,catalog};return catalog;
}
// Reads the history of the panel's contract under the revision policy and overlays chosen. Each read, the catalog's included, is kept
// only for the result, contract, account, close and choices it was made for; one the person closed the panel during is dropped.
async function history(){
  if(!result||!contract)return;
  const owner=result,selected=contract,started=marketGeneration,scope=marketSelection(),policy=historyPolicy,overlays=overlayRequests;
  const current=()=>result===owner&&contract===selected&&started===marketGeneration&&scope===marketSelection()&&policy===historyPolicy&&overlays===overlayRequests;
  historyData=undefined;historySeries=[];historyFailed=false;renderHistory();
  try{
    const catalog=await readOverlayCatalog();
    if(!contract)return;if(!current())throw staleSelection();
    contractPanel.catalog(catalog);
    const next=await api<ContractHistory>('history',{revisionPolicy:policy,snapshotId:owner.snapshotId,curve:owner.curve,...(owner.kind==='rates'?{seriesId:selected}:{contractId:selected}),...(overlays.length?{overlays}:{})});
    if(!contract)return;if(!current())throw staleSelection();
    historyBasis(policy,next,instant);
    historySeries=answeredOverlays(overlays,next.overlays,catalog,'conflict' in historyChart(next));historyData=next;
  }catch(error){if(current()){historyFailed=true;renderHistory();}throw error;}
  renderHistory();
}
function renderInputs(){
  if(!result||!result.points.length){text('inputs',result?'No price sources are needed for an empty delivery window.':'Choose a market date to view price sources.');text('input-count','');return;}
  const used=new Set(sourcePoints().flatMap(p=>p.lineage));if([...used].some(id=>!pricingFrame().observationIds.includes(id)))throw Error('A required source observation is missing from this market date.');const obs=[...used].map(observation);text('input-count',`${obs.length} prices & factors`);
  table('inputs',[isRates()?'Maturity':'Delivery period','Input','Source','Value','Units',''],obs.map(o=>{const series=row('series','id',val(o,'series_id')),c=series.contract_id?row('contracts','id',val(series,'contract_id')):undefined;return [c?deliveryLabel(val(c,'delivery_from'),val(c,'delivery_to')):data().definitions.rate_curve_points.some(p=>p.curve_code===curve&&p.series_id===series.id)?displayDate(val(data().definitions.rate_curve_points.find(p=>p.curve_code===curve&&p.series_id===series.id)!,'maturity')):'All months',sourceName(series),referenceName('sources',val(o,'source_code')),val(o,'value'),inputUnit(series),action(canCorrect()?'Correct price':'View versions',async()=>{input=val(o,'id');renderRevisions();})];}),[3]);renderRevisions();
}
function renderRevisions(){
  if(!input||!pricingFrame().observationIds.includes(input)||!sourcePoints().some(p=>p.lineage.includes(input))){$('revision-card').hidden=true;return;}
  const o=observation(input),series=row('series','id',val(o,'series_id'));$('revision-card').hidden=false;text('correction-description',`${sourceName(series)} · ${instant(val(o,'observed_at'))} · ${inputUnit(series)}`);
  const editor=canCorrect();$('correction-form').hidden=!editor;text('changes-title',`${editor?'Correct':'Versions of'} this ${isRates()?'rate':'price'}`);
  text('edit-help',isRates()?'Enter an annual decimal rate: 0.025 means 2.5%. Earlier versions stay available.':'Earlier prices stay available. Saving adds a new version for you to view.');
  if(draftFor!==input){$<HTMLInputElement>('correction-value').value=val(o,'value');draftFor=input;}
  const revisions=data().observations.filter(r=>r.series_id===o.series_id&&r.source_code===o.source_code&&r.source_event_id===o.source_event_id&&r.observed_at===o.observed_at&&r.role===o.role);
  table('revisions',['Version','Value','Saved',''],revisions.map((r,i)=>[i===0?'Original':`Change ${i}`,val(r,'value'),instant(val(r,'received_at')),r.id===input?'Viewing':action('View this version',async()=>{const next=await api<{frame:Frame}>('select-observation',{snapshotId:frame().id,observationId:r.id});clearPrices();await reload(next.frame.id);input=val(r,'id');await build();})]),[1]);
}
function pricingPoint(){
  if(!result)throw Error('Choose a market date first.');
  const point=result.points.find(p=>pointIdentity(p)===pricingContract);
  if(!point)throw Error('Choose a contract for the pricing breakdown.');return point;
}
function pricingPoints(){
  if(!result||!result.points.length)throw Error('The contract pricing details are unavailable.');
  return showAllContracts?result.points:[pricingPoint()];
}
function sourcePoints(){return sourceOnlyCurve&&result?result.points:pricingPoints();}
function drawPricingGraph(target:HTMLElement,graph:PricingGraph){renderPricingGraph(target,graph,curveName,unit);}
// A maturity's discount factor from its zero rate, under the convention the evaluated result states.
function rateFlow(point:Point){
  const convention=rateConvention();
  if(point.annualRate===undefined||point.actualDays===undefined||point.discountFactor===undefined||point.displayDiscountFactor===undefined)throw Error('The rate calculation details are unavailable.');
  const flow=el('div');flow.className='rate-flow';
  for(const [title,value,detail] of [['Zero rate',`${point.value}%`,'Annual rate'],['Time to maturity',`${point.actualDays} / 365`,convention.dayCount],['Discount factor',point.displayDiscountFactor,convention.compounding]]){const card=el('div');card.className='rate-step';card.append(el('small',title),el('strong',value),el('span',detail));flow.append(card);}
  return flow;
}
function renderDependencies(){
  sourceOnlyCurve=false;select('source-contract').closest<HTMLElement>('.pricing-toolbar')!.hidden=false;
  document.body.classList.toggle('showing-all-contracts',showAllContracts&&!!result);
  for(const id of ['source-flow','dependencies','mappings','inputs'])$(id).replaceChildren();$('revision-card').hidden=true;
  for(const id of ['source-all','pricing-all']){text(id,showAllContracts?'Show selected contract':'Show all contracts');$(id).setAttribute('aria-pressed',String(showAllContracts));}
  if(!result||!result.points.length){for(const id of ['source-flow','dependencies'])$(id).replaceChildren(el('p',result?'No remaining delivery days for this market date.':'Choose a market date to see the contract pricing breakdown.'));for(const id of ['source-contract','pricing-contract'])choose(id,[],'');return;}
  if(!result.points.some(p=>pointIdentity(p)===pricingContract))pricingContract=pointIdentity(result.points[0]);
  const points=pricingPoints(),rates=result.kind==='rates',options=result.points.map(p=>({value:pointIdentity(p),name:rates?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!)}));
  for(const id of ['source-contract','pricing-contract'])choose(id,options,pricingContract);
  const sources=document.createDocumentFragment(),dependencies=document.createDocumentFragment();
  if(rates){
    rateConvention(); // the formula below is drawn only for the convention it states
    if(showAllContracts){for(const container of [sources,dependencies]){const host=el('section');renderWorkbook(host,rateWorkbook(result));container.append(host);}}
    for(const point of points){
      if(point.annualRate===undefined||point.actualDays===undefined||point.discountFactor===undefined||point.displayDiscountFactor===undefined)throw Error('The rate calculation details are unavailable.');
      if(!showAllContracts)for(const container of [sources,dependencies]){const section=el('section');section.className='contract-flow pricing-flow';section.append(el('h4',displayDate(pointDate(point))),rateFlow(point));container.append(section);}
    }
    table('mappings',['Maturity','Calculation','Discount factor'],points.map(p=>[displayDate(pointDate(p)),`1 ÷ (1 + ${p.annualRate} × ${p.actualDays} / 365)`,p.discountFactor!]),[2]);
  }else{
    const graphs=points.map(point=>pricingGraph(result!,point));
    const workbook=showAllContracts||graphs.every(graph=>graph.nodes.length===1)?curveWorkbook(result,unit,curveName):undefined;
    sourceOnlyCurve=workbook?.connections.length===0;
    const workbookTargets:DocumentFragment[]=[];
    if(showAllContracts||sourceOnlyCurve)workbookTargets.push(sources);
    if(showAllContracts)workbookTargets.push(dependencies);
    for(const container of workbookTargets){const host=el('section');renderWorkbook(host,workbook!);container.append(host);}
    if(!showAllContracts)for(const graph of graphs)for(const container of sourceOnlyCurve?[dependencies]:[sources,dependencies]){const section=el('section');section.className='contract-flow pricing-flow';drawPricingGraph(section,graph);container.append(section);}
    select('source-contract').closest<HTMLElement>('.pricing-toolbar')!.hidden=sourceOnlyCurve;
    table('mappings',['Delivery period','Step','Curve','Calculation','Result','Units'],graphs.flatMap(graph=>graph.nodes.map((n,i)=>[deliveryLabel(graph.root.deliveryFrom,graph.root.deliveryTo),String(i+1),`${curveName(n.curve)} · ${deliveryLabel(n.deliveryFrom,n.deliveryTo)}`,pricingEquation(n,graph),n.value,unit(n.quote)])),[4]);
    $('mappings').append(el('p',`Settlement: ${settlementText()}.`));
  }
  $('source-flow').replaceChildren(sources);$('dependencies').replaceChildren(dependencies);
}

function referenceName(table:string,code:string,references=presentedReferences(data())){return referenceLabel(exactRow(references[table]??[],'code',code));}
const linkedTables:Record<string,string>={commodity_code:'commodities',fuel_commodity_code:'commodities',output_commodity_code:'commodities',currency_code:'currencies',quote_currency_code:'currencies',unit_code:'units',lot_unit_code:'units',from_unit_code:'units',to_unit_code:'units',quote_unit_code:'units',fuel_unit_code:'units',output_unit_code:'units',location_code:'locations',type_code:'location_types',exchange_code:'exchanges',clearing_house_code:'clearing_houses',source_code:'sources',qualification_source_code:'sources',product_code:'futures_products',series_code:'futures_series',clock_code:'clock_rules',market_clock_code:'clock_rules',calendar_code:'calendars',trading_calendar_code:'calendars',quantity_code:'quantity_specifications',basis_code:'measurement_bases',index_a_code:'price_index_definitions',index_b_code:'price_index_definitions',numerator_quantity_code:'quantity_specifications',denominator_quantity_code:'quantity_specifications'};
type ReferenceRows=Pick<ReferencePage,'references'|'referenceDisplay'|'referenceRevision'>;
function referenceValue(r:Row,key:string,read:ReferenceRows){
  if(r[key]===null)return '—';const value=val(r,key);if(value==='true'||value==='false')return value==='true'?'Yes':'No';if(linkedTables[key]){const linked=linkedTables[key],rows=displayReferenceRows(linked,read.references[linked]??[],read.referenceDisplay,read.referenceRevision);return referenceName(linked,value,{[linked]:rows});}
  if(key.endsWith('_at')||key==='eligible_from')return instant(value);if(key==='code')return friendly(value);
  if(/^-?\d+(\.\d+)?$/.test(value)||['name','symbol','display_code','source_series_code','zone_name','series_label','delivery_from','delivery_to','event_date','date','locator','evidence'].includes(key))return value;
  return friendly(value);
}
function clearReference(){referencePage=undefined;shownReference='';referenceEditor.reset();unitConverter.reset();$('reference').replaceChildren();text('reference-page-status','');$('reference-item').replaceChildren();$('reference-item').hidden=true;$('reference-list').hidden=false;}
const referenceSections=()=>dataSections(Object.keys(data().references)).flatMap(g=>g.sections);
// The chosen category, shown as its group and tab, with its list or one of its items (`listed`). One no longer available is an
// error, and the next opening starts on Currencies.
function referenceCategory(listed:boolean){
  const selected=referenceTable||'currencies',section=referenceSections().find(s=>s.value===selected);
  if(!section){referenceTable='';referenceNavigation.clear();throw Error('These details are unavailable. Please refresh the page.');}
  referenceTable=selected;referenceNavigation.select(selected,listed);text('data-title',section.name);text('data-description',section.consumer);
  return section;
}
const listAddress=(table:string,search:string):ReferenceAddress=>search?{table,search}:{table};
// The address of what the page shows. A move from a list, to another list or an item, is a new history entry; opening an address,
// or the page from elsewhere, replaces the entry that led to it.
function addressReference(address:ReferenceAddress){
  const hash=formatReferenceAddress(address),current=parseReferenceAddress(location.hash);shownReference=hash;
  if(location.hash===hash)return;
  if(current&&current.code===undefined)window.history.pushState(null,'',hash);else window.history.replaceState(null,'',hash);
}
// Opens a category's tab, with its rows matching `search` when one is given, at the page an item of it was opened from.
async function showReference(table:string,search:string){
  clearReference();await loadWorkspace();
  if(!referenceSections().some(s=>s.value===table)){referenceNavigation.clear();throw Error('That reference table is not available here.');}
  const offset=referenceReturn?.table===table&&referenceReturn.search===search?referenceReturn.offset:0;referenceReturn=undefined;
  referenceTable=table;$<HTMLInputElement>('reference-search').value=search;await loadReference(offset,search);
}
// The page an address names: a category's list, or one item of it with a breadcrumb back to that list.
async function openReferenceItem(address:ReferenceAddress){
  const {table,code,search=''}=address;
  if(code===undefined)return showReference(table,search);
  const from=referencePage;
  clearReference();if(contextDirty)throw Error('Apply the changed context first.');await loadWorkspace();
  if(!referenceSections().some(s=>s.value===table)){referenceNavigation.clear();throw Error('That reference table is not available here.');}
  if((keylessCategories as readonly string[]).includes(table))throw Error('Rows of this category have no page of their own. Choose the category to list them.');
  if(from)referenceReturn={table:from.table,search:from.search,offset:from.offset};
  referenceTable=table;const section=referenceCategory(false),owner=data(),context=historicalContext()??null;
  // The editor adds the item's display version to these facts once it has read it.
  const heading={list:{name:section.name,address:listAddress(table,search)},facts:[['As of',context?`${displayDate(context.effectiveOn)}${context.knownAt?`, as known at ${instant(context.knownAt)}`:''}`:'Working data']] as const};
  const current=()=>workspace===owner&&!contextDirty&&page==='reference'&&referenceTable===table;
  try{
    if(table==='futures_products'){
      const read=await api<FuturesProductRead&{context:Context|null;profile:string}>('reference/futures-product?'+new URLSearchParams({code}));
      if(!current()||!sameReferenceRead(owner,table,read,context)||read.product?.code!==code||![read.series,read.milestones,read.executionPolicies].every(Array.isArray)||!read.references)
        throw Error('The futures product is missing or inconsistent. Refresh and try again.');
      renderFuturesProduct($('reference-item'),read,{list:heading.list,facts:heading.facts,instant});
    }else{
      const read=await api<ReferencePage>('reference/catalog?'+new URLSearchParams({table,code}));
      if(!current()||!sameReferencePage(owner,table,read,context)||read.rows.length!==1||read.rows[0].code!==code)
        throw Error('The reference item is missing or inconsistent. Refresh and try again.');
      const [row]=displayReferenceRows(table,read.rows,read.referenceDisplay,read.referenceRevision);
      const addressable=(linked:string|undefined)=>linked!==undefined&&referenceSections().some(s=>s.value===linked);
      renderReferenceItem($('reference-item'),{list:heading.list,title:referenceLabel(row),facts:[['Code',code],...heading.facts]},
        publicColumns([row]).map(c=>({label:c.name,value:referenceValue(row,c.key,read),editField:c.key,...(row[c.key]!==null&&addressable(linkedTables[c.key])?{linkTo:{table:linkedTables[c.key],code:String(row[c.key])}}:{})})));
    }
    await referenceEditor.show(table,code,context);
  }catch(error){clearReference();throw error;}
  $('reference-list').hidden=true;
  addressReference(address);
}
// A reference address from the browser's location or a link on the page; one naming no category shows no rows.
async function openReferenceAddress(hash:string){
  await setPage('reference');
  const address=parseReferenceAddress(hash);
  if(address)return openReferenceItem(address);
  clearReference();referenceNavigation.clear();
  throw Error('This address names no available reference category. Choose one from Reference data.');
}
type ReferenceSearch={query:string;hits:ReferenceHit[];truncated:boolean;context:Context|null;profile:string};
async function searchReference(q:string){
  if(contextDirty)throw Error('Apply the changed context first.');await loadWorkspace();
  const owner=data(),context=historicalContext()??null;
  const found=await api<ReferenceSearch>('reference/search?'+new URLSearchParams({q}));
  if(workspace!==owner||contextDirty||page!=='reference'||found.query!==q||!Array.isArray(found.hits)||typeof found.truncated!=='boolean'||found.profile!==owner.profile||JSON.stringify(found.context)!==JSON.stringify(context))
    throw Error('The reference search is missing or inconsistent. Refresh and try again.');
  referenceNavigation.showHits(found.hits,found.truncated);
}
const unitCatalogCategories=['units','unit_conversions','dimensions','commodities','measurement_bases','quantity_specifications'];
// A reference read answers in the workspace's exact reference revision (the unit catalog's, for its categories), for this account and context.
function sameReferenceRead(owner:Workspace,table:string,read:{referenceRevision?:ReferenceRevision;profile:string;context:Context|null},context:Context|null){
  const revision=owner.definitions.revisions[0],expected=unitCatalogCategories.includes(table)?owner.unitCatalog?.revision as ReferenceRevision|undefined:{catalogId:val(revision,'reference_catalog_id'),revisionId:val(revision,'reference_revision_id')};
  return !!expected&&read.referenceRevision?.catalogId===expected.catalogId&&read.referenceRevision?.revisionId===expected.revisionId&&read.profile===owner.profile&&JSON.stringify(read.context)===JSON.stringify(context);
}
// A page of one category, in the workspace's definitions (outside the unit catalog), bounded by the server's page size.
function sameReferencePage(owner:Workspace,table:string,read:ReferencePage,context:Context|null){
  return sameReferenceRead(owner,table,read,context)&&(unitCatalogCategories.includes(table)||read.definitionRevisionId===val(owner.definitions.revisions[0],'id'))&&read.table===table
    &&read.referenceScope==='selected_table'&&read.pageSize===200&&Number.isSafeInteger(read.maximumOffset)&&typeof read.hasMore==='boolean'&&Array.isArray(read.rows)&&read.rows.length<=200&&!!read.references;
}
function renderReference(){
  if(contextDirty||!referencePage)return;
  const {table:selected,rows,offset,hasMore}=referencePage;
  if(data().unitCatalog)unitConverter.populate({...data(),references:data().unitCatalog!.references},selected==='units'||selected==='unit_conversions');else unitConverter.reset();
  const columns=publicColumns(rows),displayRows=displayReferenceRows(selected,rows,referencePage.referenceDisplay,referencePage.referenceRevision),shown=referencePage;
  // A row opens its item's page; rows of a category without codes have none.
  const open=(keylessCategories as readonly string[]).includes(selected)?undefined:(index:number)=>void act(()=>openReferenceItem({table:selected,code:String(rows[index].code),...shown.search?{search:shown.search}:{}}));
  table('reference',columns.map(c=>c.name),displayRows.map(r=>columns.map(c=>referenceValue(r,c.key,shown))),[],undefined,open);
  const filter=$('reference').querySelector<HTMLInputElement>('.table-search')!;filter.placeholder='Filter this page…';filter.setAttribute('aria-label','Filter this page');
  text('reference-page-status',rows.length?`Rows ${offset+1}–${offset+rows.length}${hasMore?canAdvanceReference()?' · more available':' · Refine your search to see more results.':''}`:'No rows on this page.');
}
async function loadReference(offset=0,search=''){
  clearReference();if(contextDirty)throw Error('Apply the changed context first.');await loadWorkspace();
  const owner=data(),selected=referenceCategory(true).value,context=historicalContext()??null;
  const query=new URLSearchParams({table:selected,offset:String(offset),search});
  const next=await api<ReferencePage>('reference/catalog?'+query);
  if(workspace!==owner||contextDirty||page!=='reference'||referenceTable!==selected||!sameReferencePage(owner,selected,next,context)||next.offset!==offset||next.search!==search||next.maximumOffset<offset)
    throw Error('The reference page is missing or inconsistent. Refresh and try again.');
  referencePage=next;try{renderReference();}catch(error){clearReference();throw error;}
  addressReference(listAddress(selected,search));
}
function marketSelection(){return JSON.stringify([administrationScope,currentProfile,select('snapshot').value,curve,contextDirty]);}
function validateWorkspace(next:Workspace,owner:MarketNavigation,started:number,scope:string){
  if(next.referenceScope!=='definition_closure'||next.observationScope!=='on_demand'||!Array.isArray(next.observations)||next.observations.length||!Array.isArray(next.curveNames))throw Error('The market catalog is missing or inconsistent. Refresh and try again.');
  const revision=next.definitions?.revisions?.[0];
  const sameRevision=revision&&val(revision,'catalog_id')===owner.definitionRevision.catalogId&&val(revision,'id')===owner.definitionRevision.revisionId&&val(revision,'reference_catalog_id')===owner.referenceRevision.catalogId&&val(revision,'reference_revision_id')===owner.referenceRevision.revisionId;
  const summarize=({id,name,at,originCloseId}:FrameSummary)=>({id,name,at,originCloseId:originCloseId??null});
  const summaries=next.frames?.map(summarize);
  if(navigation!==owner||started!==marketGeneration||scope!==marketSelection()||contextDirty||next.profile!==owner.profile||!sameRevision||
     JSON.stringify((next as Workspace & {context?:Context}).context??null)!==JSON.stringify(owner.context)||JSON.stringify(summaries)!==JSON.stringify(owner.frames.map(summarize))||
     JSON.stringify(next.initialFrameIds)!==JSON.stringify(owner.initialFrameIds)||next.futuresConvention!==owner.futuresConvention)
    throw Error('The account or market selection changed, or the market catalog is inconsistent. Reload before continuing.');
  return next;
}
async function loadWorkspace(){
  if(workspace)return;
  const owner=directory(),started=marketGeneration,scope=marketSelection();
  const next=await api<Workspace>('workspace/catalog');workspace=validateWorkspace(next,owner,started,scope);
}
async function loadNavigation(){
  const started=++marketGeneration,profile=currentProfile,scope=administrationScope;
  workspace=undefined;navigation=undefined;clearReference();referenceNavigation.clear();setCurveNames([]);$('curves').replaceChildren();
  const next=validateMarketNavigation(await api<MarketNavigation>('workspace/navigation'));
  if(started!==marketGeneration||currentProfile!==profile||administrationScope!==scope||next.profile!==profile||contextDirty)
    throw Error('The account or market selection changed. Reload before continuing.');
  navigation=next;setCurveNames(next.curveNames);if(next.live)connectLive(next.live);else stopLive();
}
function stopLive(){liveStream?.close();liveUpdates?.stop();liveStream=undefined;liveUpdates=undefined;liveId='';lastLiveUpdate=undefined;$('live-status').hidden=true;$('live-retry').hidden=true;}
function showLiveStatus(status:LiveStatus){
 if(status.updatedAt)liveSucceeded=status.updatedAt;
 const last=status.updatedAt??liveSucceeded;
 const badge=$('live-status');badge.hidden=false;badge.dataset.state=status.state;
 const stamp=last?` · last refreshed ${last.toLocaleTimeString(undefined,{timeZone:prefs.timeZone})}`:' · waiting for a successful refresh';
 badge.textContent=(status.state==='live'?'Live':status.state==='updating'?'Updating · previous values may be stale':'Stale · disconnected or refresh failed')+stamp;
 $('live-retry').hidden=status.state!=='stale';
}
async function refreshLiveScreen(update:LiveContextUpdate){
 if(liveId!==update.live.id)throw Error('The live selection changed.');
 await loadNavigation();
 if(liveId!==update.live.id||!navigation?.live||!navigation.context)throw Error('The live selection changed.');
 renderFrames(navigation.frames[0]?.id??'');dateEditor.showContext(navigation.context);
 jobs.contextChanged();
 // The open contract panel is read again with the page (D188): its history at the new generation, or closed when the new result has no such contract.
 if(page==='market'){if(curve){const open=contract;historyData=undefined;historySeries=[];await build();if(open&&contract===open)await history();}await loadBoard();}
 else if(page==='custom-curves')await customCurves.liveRefresh();
 else if(page==='futures')await futures.liveRefresh();
 else if(page==='reference')await loadReference();
 text('environment-context-note','Live corrections · fixed effective and market dates. Every risk run and queued job retains its admitted context.');
}
function connectLive(selection:LiveSelection){
 if(liveId===selection.id&&liveStream)return;
 const previous=liveId===selection.id?liveSucceeded:undefined;
 stopLive();liveSucceeded=previous;liveId=selection.id;showLiveStatus({state:'updating'});
 let cooldown=250;
 liveUpdates=new LiveUpdates({busy:()=>busy||document.hidden,schedule:work=>{const timer=setTimeout(work,cooldown);return()=>clearTimeout(timer);},status:showLiveStatus,
  apply:async update=>{if(liveId!==update.live.id)throw Error('The live selection changed.');busy=true;const started=performance.now();syncControls();try{permissions=(await api<{permissions:string[]}>('access')).permissions;await refreshLiveScreen(update);}finally{cooldown=Math.max(250,Math.min(10000,(performance.now()-started)*2));busy=false;syncControls();}}});
 const stream=liveStream=new EventSource(apiRoot+'market/live/events?selection='+encodeURIComponent(selection.id));
 stream.addEventListener('context',event=>{
  if(liveStream!==stream)return;
  try{const update=liveContextUpdate(JSON.parse((event as MessageEvent<string>).data),selection.id);lastLiveUpdate=update;liveUpdates?.reconnected();liveUpdates?.offer({...update,generation:update.live.generation});}
  catch{liveUpdates?.disconnected();}
 });
 stream.onerror=()=>{if(liveStream===stream)liveUpdates?.disconnected();};
}
async function reload(selected:string){await loadNavigation();renderFrames(selected);if(curve)await loadWorkspace();renderContext();await loadBoard();}
async function updateFilters(){
  prefs.browseBy=select('browse-by').value as Preferences['browseBy'];prefs.market=select('commodity').value||null;persist();
  if(curve&&!navigationGroups(directory(),prefs.browseBy,prefs.market,prefs.marketType).some(g=>g.curves.some(c=>c.code===curve))){curve='';invalidateMarketDates();clearPrices();}
  renderContext();await loadBoard();syncControls();
}
async function openProfile(){
  try{
    const current=(await api<{permissions:string[]}>('access')).permissions;
    if(JSON.stringify([...current].sort())!==JSON.stringify([...permissions].sort()))await loadIdentity();
  }catch(error){if((error as {code?:string}).code==='access_denied'){text('profile-access','Workspace access could not be verified.');await loadIdentity();}throw error;}
  await profileChoices();
  choose('zone',zoneChoices(prefs.timeZone),prefs.timeZone);$<HTMLInputElement>('show-assistant').checked=!prefs.assistantHidden;select('preferred-browse').value=prefs.browseBy;$<HTMLDialogElement>('profile-dialog').showModal();
}
$('sidebar-toggle').onclick=()=>{if(busy)return;prefs.sidebarCollapsed=!prefs.sidebarCollapsed;renderLayout();persist();};
$('library-toggle').onclick=()=>{if(busy)return;prefs.libraryCollapsed=!prefs.libraryCollapsed;renderLayout();persist();};
for(const {id} of sidebarGroups)groupNode(id).ontoggle=()=>{if(!currentProfile||prefs.sidebarCollapsed)return;const closed=prefs.collapsedMenus.includes(id),open=groupNode(id).open;if(closed===!open)return;prefs.collapsedMenus=open?prefs.collapsedMenus.filter(k=>k!==id):[...prefs.collapsedMenus,id];persist();};
function profileError(message=''){text('profile-error',message);$('profile-error').hidden=!message;}
// The page behind an open modal is inert, so its actions report within the modal.
async function profileAction(control:HTMLElement,work:()=>Promise<void>){
  let failed=false;
  await act(async()=>{profileError();try{await work();}catch(error){if(!$<HTMLDialogElement>('profile-dialog').open)throw error;profileError(errorMessage(error));failed=true;}});
  if(failed&&$<HTMLDialogElement>('profile-dialog').open)control.focus();
}
$('profile-dialog').addEventListener('close',()=>profileError());
$('profile-open').onclick=()=>void act(openProfile);for(const id of ['profile-close','profile-cancel'])$(id).onclick=()=>$<HTMLDialogElement>('profile-dialog').close();
$('preferences-form').onsubmit=event=>{event.preventDefault();void (async()=>{if(!await mayLeaveForDialog('Your preferences were not saved','keeps'))return;await profileAction($('profile-save'),async()=>{const zone=select('zone').value;if(!validTimeZone(zone))throw Error('Choose a valid time zone.');const selected=select('snapshot').value,zoneChanged=zone!==prefs.timeZone;prefs.timeZone=zone;if(zoneChanged){workspaceCloses=undefined;closeCheckDue=true;}await prepareAmountDisplay({points:result?.points,historyData},request=>transport<string[]>('amounts/display',request),decimalTick());prefs.browseBy=select('preferred-browse').value as Preferences['browseBy'];prefs.assistantHidden=!$<HTMLInputElement>('show-assistant').checked;persist();assistant.sync();if(navigation){renderFrames(selected);renderContext();renderPrices();renderReference();schedules.refresh();dateEditor.refresh();power.refresh();}renderTimeZones();customCurves.refresh();jobs.refresh();administration.refresh();aiSettings.refresh();parties.refresh();intervalSeries.refresh();intervalCurves.refresh();ppa.refresh();futures.refresh();if(page==='futures'&&select('snapshot').value)await futures.load({background:false});$<HTMLDialogElement>('profile-dialog').close();});})();};
function setHostMode(mode:string){
  if(mode!=='hosted-pilot'&&mode!=='local-synthetic-preview')throw Error('The workspace mode could not be verified. Please refresh.');
  hostedPilot=mode==='hosted-pilot';
  text('workspace-host-mode',hostedPilot?'Private pilot':'Synthetic preview');
  text('workspace-mode-description',hostedPilot?'Private trading workspace.':'Sample markets on a disposable local database.');
  text('workspace-footer',hostedPilot?'Private workspace. Generated marks and sample trades are labeled.':'Synthetic sample data. Every change stays in this disposable local preview.');
}
const bootstrapPath='bootstrap?includeFrames=false';
const startupBootstrapPath=bootstrapPath+'&includeAccess=true';
async function profileChoices(initial?:ProfileChoices){const boot=initial??await api<ProfileChoices>(bootstrapPath);setHostMode(boot.mode);$('preview-access').hidden=hostedPilot;$('sign-out').hidden=!hostedPilot;choose('profile',[...boot.profiles,...boot.customProfiles.filter(p=>!boot.profiles.some(base=>base.id===p.id))].map(p=>({value:p.id,name:p.name})),currentProfile);const name=select('profile').selectedOptions[0]?.textContent??'Current user';text('profile-label',name);text('profile-avatar',name.slice(0,1));text('profile-title',name);text('profile-access','Current permissions: '+(permissions.length?permissions.map(p=>friendly(p.replace(/([a-z])([A-Z])/g,'$1 $2'))).join(', '):'No workspace access'));$('profile-open').setAttribute('aria-label','Profile · '+name);}
$('sign-out').onclick=()=>void (async()=>{if(!await mayLeaveForDialog('You were not signed out'))return;await act(async()=>{await api('logout',{});location.assign('/auth');});})();
async function loadIdentity(initialAccess?:unknown){
  $('curve-price-navigation').hidden=true;customCurves.reset();$('custom-curves-page').hidden=true;jobs.reset();$('jobs-page').hidden=true;
  deliveryPrices.reset();intervalSeries.reset();intervalCurves.reset();ppa.reset();referenceEditor.reset();$('delivery-page').hidden=true;$('interval-series-page').hidden=true;$('interval-curves-page').hidden=true;parties.reset();unitConverter.reset();futures.reset(null);blotterViews={listed:[],shown:null};renderBlotterViews();power.reset();touProducts.reset();schedules.reset();dateEditor.reset();administration.reset();aiSettings.reset();agentGrants.reset();contextDirty=false;$('parties-page').hidden=true;$('power-page').hidden=true;$('futures-page').hidden=true;$('no-access-page').hidden=true;
  curve='';clearPrices();clearReference();referenceTable='';referenceReturn=undefined;referenceNavigation.clear();marketGeneration++;workspace=undefined;navigation=undefined;setCurveNames([]);permissions=[];choose('snapshot',[{value:'',name:'Choose a date…'}],'');marketCalendar.reset();workspaceCloses=undefined;closeBasis=undefined;select('snapshot').closest<HTMLElement>('.date-control')!.hidden=true;$('curves').replaceChildren();$('market-page').hidden=true;$('reference-page').hidden=true;$('schedules-page').hidden=true;$('curve-date-editor').hidden=true;$('admin-page').hidden=true;$('ai-page').hidden=true;$('date-context').hidden=true;
  if(initialAccess===undefined)permissions=(await api<{permissions:string[]}>('access')).permissions;
  else{
    const access=initialAccess as StartupAccess|null;
    if(!access||access.profile!==currentProfile||!Array.isArray(access.permissions)||!access.permissions.every(p=>typeof p==='string'))throw Error('The workspace access metadata is missing or inconsistent. Refresh and try again.');
    permissions=[...access.permissions];
  }
  loadProfile();renderTimeZones();assistant.reset();
  // Navigation follows current permissions; the backend still authorizes every operation.
  const read=can('ReadMarketData'),author=read&&(can('WriteMarketDefinitions')||can('SealMarketDefinitions'));
  for(const group of sidebarGroups){
    for(const page of group.pages)$('nav-'+page).hidden=!pageAvailable(page,permissions);
    groupNode(group.id).hidden=group.pages.every(page=>$('nav-'+page).hidden);
  }
  $('schedule-inspector').hidden=!author;
  const first=firstPage(permissions);
  // The newest close is chosen between the directory and the first page, which then reads it. A historical view the session
  // kept is not working data, so it chooses nothing.
  let opening:CloseChoice={kind:'none'};
  if(read){await loadNavigation();renderFrames('');renderContext();if(!historicalContext()){opening=await selectNewestClose();await initializeContext();}}else renderLayout();
  // A reference address in the location opens that page in place of the first; failing to open it keeps the rest of the opening.
  const address=location.hash.startsWith('#reference')&&pageAvailable('reference',permissions)?location.hash:undefined;
  let unopened:unknown;
  if(first!==null){
    if(address===undefined)await showPage(first);else await openReferenceAddress(address).catch(error=>{unopened=error;});
    if(!$('nav-futures').hidden)await futures.views();
    closeCheckDue=false; // the read that chose the close is this page's check for a newer one
    if(opening.kind==='choose')askNewestClose(opening.date);
  }
  else{$('no-access-page').hidden=false;text('page-title','Workspace access');text('status','This account has no current workspace permissions.');}
  if(unopened!==undefined)throw unopened;
}
$('open').onclick=()=>void (async()=>{if(!await mayLeaveForDialog('The user was not switched'))return;await profileAction($('open'),async()=>{text('environment-context-note','Current workspace · exact selected inputs');const next=select('profile').value;await api('profile',{profile:next});currentProfile=next;await loadIdentity();await profileChoices();$<HTMLDialogElement>('profile-dialog').close();});})();
// A newly selected As of close replaces what the previous one showed, and the badge takes its basis afresh.
function closeChanged(){
  shownClose=select('snapshot').value;closeBasis=undefined;
  marketCalendar.selectedChanged();customCurves.snapshotChanged();parties.reset();futures.reset('inputs');clearPrices();
  // The board and the library show nothing of the previous close while the new one is read.
  if(navigation)renderBrowse();renderMarketBoard();
}
async function loadSelectedClose(){
  if(!select('snapshot').value)return;
  if(page==='parties')await parties.load();else if(page==='futures')await futures.load();else if(page==='power'){power.reset();await power.load();}else if(page==='custom-curves')await customCurves.load();else if(page==='interval-curves')await intervalCurves.load();else if(page==='market'){if(curve)await build();await loadBoard();}
}
// A close the person chooses waits for them to agree to what choosing it gives up. The hidden select that every read and command takes its close from
// stays on the close the page shows until they do, so that nothing made while the question is open uses a close they have not confirmed, and keeping
// the amendment has nothing to put back. The calendar chooses through here, and so does anything that stands in for it with a change event.
function chooseClose(id:string){
  select('snapshot').value=shownClose;
  leaving(async()=>{select('snapshot').value=id;closeChanged();await loadSelectedClose();},'closes');
}
$('snapshot').onchange=()=>chooseClose(select('snapshot').value);
$('example').onclick=()=>void act(async()=>{curve='';clearPrices();await currentInputs();const example=directory().example;if(!example)throw Error('No sample is configured.');const f=directory().frames.find(f=>directory().initialFrameIds.includes(f.id)&&new Date(f.at).getTime()===new Date(example.at).getTime());if(!f)throw Error('These sample prices are unavailable.');prefs.market=null;prefs.marketType='all';prefs.tab='prices';persist();curve=example.curve;clearPrices();renderFrames(f.id);await initializeContext();await build();});
$('browse-by').onchange=()=>void act(updateFilters);$('commodity').onchange=()=>void act(updateFilters);
document.querySelectorAll<HTMLButtonElement>('[data-type]').forEach(b=>b.onclick=()=>void act(async()=>{prefs.marketType=b.dataset.type as Preferences['marketType'];await updateFilters();}));
document.querySelectorAll<HTMLButtonElement>('.tab').forEach(b=>b.onclick=()=>{if(busy)return;prefs.tab=b.dataset.view as Preferences['tab'];persist();showView(prefs.tab);assistant.sync();});
const schedules=setupShortcutEditor(act,{time:api,load:()=>api('schedules'),preview:draft=>api('schedules/preview',draft),keep:draft=>api('schedules/keep',draft),resolve:body=>api('schedules/resolve',body)},()=>busy,()=>prefs.timeZone);
function dirtyContext(){stopLive();marketGeneration++;jobs.contextChanged();customCurves.contextChanged();deliveryPrices.reset();intervalSeries.reset();intervalCurves.reset();ppa.reset();parties.reset();clearReference();referenceNavigation.clear();futures.reset('inputs');power.reset();contextDirty=true;clearPrices();if(navigation)renderBrowse();renderMarketBoard();text('environment-context-note','Context changed · choose View this context to apply it.');}
const parties=setupPartyEditors({api,act,busy:()=>busy,zone:()=>prefs.timeZone,profile:()=>administrationScope+':'+currentProfile,source:()=>{if(contextDirty)throw Error('Apply the changed context first.');return frame().id;}});
const administration=setupAdministration(act,api,()=>busy,()=>prefs.timeZone,()=>administrationScope+':'+currentProfile,profileChoices);
// A person's own agent grants, in the profile dialog (docs/ai-assistant.md §7.1).
const agentGrants=setupAgentGrants({api,act,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile});
$('profile-dialog').addEventListener('close',agentGrants.hide);
// Settings › AI, each tab offered by its own permission (docs/ai-assistant.md §9).
const aiSettings=setupAiSettings({api,act,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile,tabs:()=>aiTabs(permissions)});
const dateEditor=setupDateEditor(act,api,()=>busy,()=>prefs.timeZone,dirtyContext,()=>administrationScope+':'+currentProfile,{holds:holdsEdits,mayChange:async()=>!busy&&await mayLeave()},work=>act(work,{keepNotice:true}));
$('date-context').addEventListener('input',dirtyContext);
// Opening the time controls is how a person starts to change the context the Blotter is read in, which clears it as the first change is made:
// so it asks first, as every other way out of an amendment does, and the controls stay closed when the person keeps the amendment, or while
// the page is busy, which cannot be asked. Controls that are open already ask before each change of theirs (the date editor's guard).
$<HTMLElement>('date-context').querySelector('summary')!.addEventListener('click',event=>{
  const controls=$<HTMLDetailsElement>('date-context');
  if(controls.open||!holdsEdits())return;
  event.preventDefault();
  if(busy)return;
  void mayLeave().then(agreed=>{if(agreed)controls.open=true;});
});
document.addEventListener('input',refreshContextPanels);
document.addEventListener('change',refreshContextPanels);
async function applyContext(){
  jobs.contextChanged();
  const context=await dateEditor.context();
  await viewContext(await api<{context:Context}>('market/context',{effectiveOn:context.effectiveOn,knownAt:context.knownAt,mode:'at',selection:'latest_correction'}));
}
async function viewContext(snapshot:{context:Context}){
  futures.reset('inputs');dateEditor.setContext(snapshot.context);contextDirty=false;clearPrices();await reload('');
  text('environment-context-note','Historical view · working snapshots use the latest known corrections; stored closes retain their original inputs. Changes are disabled.');
}
$('close-view-context').onclick=()=>{$<HTMLDetailsElement>('date-context').open=false;};
$('apply-view-context').onclick=()=>leaving(async()=>{await applyContext();if(page==='reference')await loadReference();else if(page==='custom-curves')await customCurves.load();else if(page==='interval-series')await intervalSeries.load();else if(page==='interval-curves')await intervalCurves.load();});
async function currentInputs(){jobs.contextChanged();customCurves.contextChanged();intervalSeries.reset();intervalCurves.reset();ppa.reset();parties.reset();unitConverter.reset();futures.reset('inputs');power.reset();await api('market/current',{});contextDirty=false;dateEditor.clearContext();clearPrices();await reload('');text('environment-context-note','Working data uses current knowledge. These controls select a historical context when applied.');}
// Returning to working data opens it on its newest close, as opening the workspace does (D217, D219), and the page that is
// showing reads it. A latest instant with several closes opens the calendar on its date and selects nothing.
async function returnToWorking(){
  await currentInputs();
  const choice=await selectNewestClose();await initializeContext();
  if(page==='reference')await loadReference();else if(page==='interval-series')await intervalSeries.load();
  else if(choice.kind==='single')await loadSelectedClose();
  else if(page==='custom-curves')await customCurves.load();else if(page==='interval-curves')await intervalCurves.load();
  if(choice.kind==='choose')askNewestClose(choice.date);
}
// The time controls of working data show the selected close's effective date at current knowledge, ready to be changed (D217).
async function initializeContext(){await dateEditor.initialize(directory().frames.find(f=>f.id===select('snapshot').value)?.at.slice(0,10)??'');}
$('current-view-context').onclick=()=>leaving(returnToWorking);
$('return-working').onclick=()=>leaving(returnToWorking);
$('follow-live').onclick=()=>leaving(async()=>{
 const selected=await api<LiveContextUpdate>('market/live',{snapshotId:frame().id,policy:'selected_facts_latest_committed_corrections_v1'});
 if(navigation){navigation.context=selected.context;navigation.live=selected.live;}
 contextDirty=false;connectLive(selected.live);lastLiveUpdate=selected;liveUpdates?.offer({...selected,generation:selected.live.generation});
},'keeps');
$('live-retry').onclick=()=>{if(liveStream?.readyState===EventSource.CLOSED&&navigation?.live){liveStream=undefined;connectLive(navigation.live);}else if(lastLiveUpdate)liveUpdates?.offer({...lastLiveUpdate,generation:lastLiveUpdate.live.generation},true);};
// The contract panel of Curves & prices docks beside the page when the main area has the room and the assistant is closed; Curve builder
// shows its views' tab groups side by side only from a main area this wide (curve-dock.ts).
const main=document.querySelector('main')!,phone=window.matchMedia('(max-width:760px)'),dockedGroupsFrom=960;
const panelDocked=()=>panelPlacement(main.clientWidth,document.body.classList.contains('assistant-open'))==='docked';
const customCurves=setupCustomCurves($('custom-curves-page'),{api,act,marketScopeChanged:invalidateMarketDates,unsavedChanged:()=>{if(initialWorkspaceReady)syncUnloadQuestion();},narrow:()=>main.clientWidth<dockedGroupsFrom,assistant:{available:()=>can('UseAgents'),draft:text=>assistant.draft(text)},busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile,historical:()=>contextDirty||!!historicalContext(),source:()=>{if(contextDirty)throw Error('Apply the changed market context first.');return {snapshotId:frame().id,context:historicalContext()};}});
// The hidden select remains the shared exact-frame adapter consumed by the
// existing pricing, trading and knowledge-context widgets. The calendar is its UI.
const marketCalendar=setupMarketCalendar($('market-date-calendar'),{
 zone:()=>prefs.timeZone,busy:()=>busy,enabled:()=>!!navigation&&can('ReadMarketData')&&!contextDirty,
 selected:()=>select('snapshot').value,current:()=>navigation?.frames.find(frame=>frame.id===select('snapshot').value),
 open:()=>void act(()=>openMarketCalendar()),newer:()=>leaving(openNewestClose,'closes'),select:chooseClose
});
const deliveryPrices=setupPowerImports({zone:()=>prefs.timeZone,api,act,busy:()=>busy,instant});
const ppa=setupPpa({api,act,busy:()=>busy,zone:()=>prefs.timeZone,actor:()=>administrationScope+':'+currentProfile,can,historical:()=>contextDirty||!!historicalContext()});
const intervalCurves=setupIntervalCurves({api,act,busy:()=>busy,zone:()=>prefs.timeZone,frame:()=>frame().id,unit:async quote=>{await loadWorkspace();return unit(quote);}});
const intervalSeries=setupIntervalSeries({api,act,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile});
const jobs=setupJobs({api,act,busy:()=>busy,identity:()=>administrationScope+':'+currentProfile,instant,zone:()=>prefs.timeZone});
// A committed label changes current curves' names and units as well as the item's page: the market is read again, as after a correction,
// and so is the page.
const referenceEditor=setupReferenceEditor({host:$('reference-item'),api,act,busy:()=>busy,identity:()=>administrationScope+':'+currentProfile,instant,onCommitted:async()=>{
 const address=parseReferenceAddress(shownReference),selected=select('snapshot').value;
 clearPrices();await reload(selected);if(address)await openReferenceItem(address);
}});
const power=setupPowerView({api,act,zone:()=>prefs.timeZone,name:curveName,source:()=>{if(contextDirty)throw Error('Apply the changed market context first.');const f=frame();return {snapshotId:f.id,curve,name:instant(f.at)};}});
const unitConverter=setupUnitConverter({api,act,busy:()=>busy});
// The knowledge the Blotter is read at: working data at current knowledge, the selected historical view, or live corrections.
const knowledgeView=()=>navigation?.live?'live' as const:historicalContext()?'historical' as const:'working' as const;
const futures=setupFutures({readFailed:error=>{text('error',errorMessage(error));$('error').hidden=false;text('status','Please check the message above.');},onViews:(listed,shown)=>{blotterViews={listed,shown};renderBlotterViews();},newPpa:async()=>{await setPage('ppa');await ppa.load();$('page-title').tabIndex=-1;$('page-title').focus();},api,act,busy:()=>busy,canRunRisk:()=>permissions.includes('RunScenario'),bookingUnavailableReason:()=>bookingFallback({contextDirty,view:knowledgeView(),canBook:permissions.includes('BookTrade')}),knowledge:knowledgeView,zone:()=>prefs.timeZone,profile:()=>administrationScope+':'+currentProfile,onReadContext:(at,known)=>text('header-calculation-time',`As of ${instant(at)} · knowledge cutoff ${instant(known)}`),notice,definitionRevision:()=>directory().definitionRevision.revisionId,curveListed:code=>!!navigation?.curves.some(c=>c.code===code),openCurve:(code,contractId)=>openCurve(code,contractId),reported:()=>assistant.sync(),unsavedChanged:()=>syncUnloadQuestion(),source:()=>{if(contextDirty)throw Error('Apply the changed market context first.');return {snapshotId:frame().id,convention:directory().futuresConvention,context:historicalContext()};}});
// The contract panel beside the curve's chart and table: docked when the main area has the room and the assistant is closed, an overlay
// otherwise, and on a phone a sheet over the page. Closing it returns focus to the contract's row, or else to what opened it.
const contractPanel=setupContractPanel($('contract-panel'),{
  docked:panelDocked,sheet:()=>phone.matches&&!$('market-page').hidden,
  closed:()=>{const row=openRow();forgetContract();markOpenRow();(row??(contractOpener?.isConnected?contractOpener:undefined))?.focus();contractOpener=undefined;},
  policy:policy=>void act(async()=>{historyPolicy=policy;await history();}),
  overlays:requests=>void act(async()=>{overlayRequests=requests;await history();})});
let mainWidth=main.clientWidth;
new ResizeObserver(()=>{if(main.clientWidth!==mainWidth){mainWidth=main.clientWidth;contractPanel.place();customCurves.place();}}).observe(main);
new MutationObserver(()=>{contractPanel.place();customCurves.place();}).observe(document.body,{attributes:true,attributeFilter:['class']});
const touProducts=setupTouProducts(act,api,()=>busy,()=>prefs.timeZone,async()=>{power.reset();if(select('snapshot').value)await power.load();});
const referenceNavigation=setupReferenceNavigation({host:$('reference-page'),groups:()=>dataSections(Object.keys(data().references)),
  onSelect:table=>act(async()=>{referenceTable=table;$<HTMLInputElement>('reference-search').value='';await loadReference();}),
  onSearch:q=>act(()=>searchReference(q))});
// A reference address the person follows, types or returns to with Back opens what it names.
window.addEventListener('hashchange',()=>{
  const hash=location.hash;
  if(!hash.startsWith('#reference')||page==='reference'&&hash===shownReference)return;
  leaving(async()=>{if(contextDirty)await applyContext();await openReferenceAddress(hash);});
});
$('reference-search-form').onsubmit=event=>{event.preventDefault();void act(()=>loadReference(0,$<HTMLInputElement>('reference-search').value));};
$('reference-previous').onclick=()=>void act(()=>loadReference(Math.max(0,referencePage!.offset-referencePage!.pageSize),referencePage!.search));
$('reference-next').onclick=()=>void act(()=>loadReference(referencePage!.offset+referencePage!.pageSize,referencePage!.search));
for(const id of ['source-contract','pricing-contract'])select(id).onchange=()=>void act(async()=>{pricingContract=select(id).value;input='';draftFor='';renderDependencies();renderInputs();});
for(const id of ['source-all','pricing-all'])$(id).onclick=()=>void act(async()=>{showAllContracts=!showAllContracts;renderDependencies();renderInputs();});
$('curve-all-markets').onclick=()=>chooseCurve('');
$('correction-form').onsubmit=event=>{event.preventDefault();void act(async()=>{const current=frame(),next=await api<{frame:Frame;observationId:string}>('corrections',{snapshotId:current.id,observationId:input,value:$<HTMLInputElement>('correction-value').value});closeCheckDue=true;clearPrices();await reload(next.frame.id);input=next.observationId;await build();notice('Your change was saved. The market date now shows the corrected prices. Earlier versions are still available.');});};
// Opens a page as its navigation does. The assistant's moves and Back open pages the same way.
async function showPage(next:string){
  switch(next){
    case 'market':await setPage('market');await loadBoard();return;
    case 'custom-curves':await setPage('custom-curves');await customCurves.load();return;
    case 'delivery':await setPage('delivery');await deliveryPrices.load();return;
    case 'interval-series':await setPage('interval-series');await intervalSeries.load();return;
    case 'interval-curves':await setPage('interval-curves');await intervalCurves.load();return;
    case 'ppa':await setPage('ppa');await ppa.load();return;
    case 'jobs':await setPage('jobs');await jobs.load();return;
    case 'power':await setPage('power');power.reset();await touProducts.load();if(select('snapshot').value)await power.load();return;
    case 'reference':await setPage('reference');$<HTMLInputElement>('reference-search').value='';await loadReference();return;
    case 'schedules':await setPage('schedules');await schedules.load();await dateEditor.load();return;
    case 'configuration':await setPage('configuration');await dateEditor.load();return;
    case 'parties':await setPage('parties');await parties.load();return;
    case 'admin':await setPage('admin');await administration.load();return;
    case 'ai':await setPage('ai');await aiSettings.load();return;
    case 'futures':await setPage('futures');futures.reset('read');if(select('snapshot').value)await futures.load();return;
    default:throw Error('This page could not load. Please refresh.');
  }
}
// Pages that read the selected historical view apply a changed one before they open.
const readsContext=['market','custom-curves','delivery','reference','configuration','parties','futures'];
const navigate=(next:string)=>()=>leaving(async()=>{if(contextDirty&&readsContext.includes(next))await applyContext();await showPage(next);});
for(const name of ['market','custom-curves','delivery','interval-series','interval-curves','jobs','power','reference','schedules','configuration','parties','admin','ai','futures'])
  $('nav-'+name).onclick=navigate(name);
// The saved Blotter views the person lists in the sidebar, each under the Blotter, which is marked while none of them is the view it shows.
let blotterViews:{listed:string[];shown:string|null}={listed:[],shown:null};
function renderBlotterViews(){
  const shown=page==='futures'&&blotterViews.listed.includes(blotterViews.shown??'')?blotterViews.shown:null;
  $('nav-futures').classList.toggle('active',page==='futures'&&shown===null);
  $('nav-blotter-views').replaceChildren(...blotterViews.listed.map(name=>{
    const entry=document.createElement('button'),icon=document.createElement('span'),label=document.createElement('span');
    entry.type='button';entry.className='nav-item nav-view';entry.classList.toggle('active',name===shown);entry.title=entry.ariaLabel=`Blotter view · ${name}`;entry.disabled=busy;
    icon.className='nav-icon';icon.ariaHidden='true';icon.textContent='▤';label.className='nav-text';label.textContent=name;entry.append(icon,label);
    entry.onclick=()=>leaving(async()=>{if(contextDirty)await applyContext();await setPage('futures');futures.reset('read');await futures.openView(name);if(select('snapshot').value)await futures.load();});
    return entry;
  }));
}
$('curve-power').onclick=navigate('power');
$('curve-price-contracts').onclick=navigate('market');

// What the page shows, as the assistant's Looking at reports it and an ask sends it (docs/ai-assistant.md §8, §10.1).
function pageState():PageState{
  switch(page){
    case 'market':return {page,curve:curve||null,tab:prefs.tab};
    case 'power':return {page,frame:!!select('snapshot').value};
    case 'reference':return {page,table:referencePage?.table??'',search:referencePage?.search??''};
    case 'parties':return {page,kind:select('party-kind').value};
    case 'futures':return {page,...futures.report()};
    case 'jobs':return {page,job:jobs.shown()};
    case 'ppa':return {page,...ppa.shown()};
    case 'custom-curves':return {page,...customCurves.report()};
    case 'configuration':case 'schedules':case 'delivery':case 'interval-series':case 'interval-curves':case 'admin':case 'ai':return {page};
    default:throw Error('This page could not load. Please refresh.');
  }
}
function pageContext():PageContext{
  if(contextDirty)return {mode:'unapplied'};
  const chosen=select('snapshot').value,shown=chosen?navigation?.frames.find(f=>f.id===chosen):undefined,frameLabel=shown?instant(shown.at):null,view=historicalContext();
  return view?{mode:'retained',contextId:view.contextId!,effectiveOn:view.effectiveOn,knownAtLabel:instant(view.knownAt!),frameId:shown?.id??null,frameLabel}
    :{mode:'current',frameId:shown?.id??null,frameLabel};
}
const place=():Place=>({page,report:pageReport(pageState()),context:pageContext()});
// Edits not yet saved, and requests whose outcome is unconfirmed, on the page a move would leave.
function unsaved(){
  switch(page){
    case 'ppa':return ppa.unsaved();
    case 'futures':return futures.unsaved();
    case 'parties':return parties.unsaved();
    case 'reference':return referenceEditor.unsaved();
    case 'configuration':return dateEditor.contractsUnsaved();
    case 'schedules':return schedules.unsaved()||dateEditor.datesUnsaved();
    case 'custom-curves':return customCurves.unconfirmed();
    case 'interval-series':return intervalSeries.unconfirmed();
    case 'jobs':return jobs.unconfirmed();
    case 'admin':return administration.unconfirmed();
    case 'ai':return aiSettings.unconfirmed();
    default:return false;
  }
}
// Opens a target's page with what it selects: the curve directory, a curve or its inputs, a reference table, a party
// kind, a Blotter view or a trade, or on Curve builder a proposed curve or a draft the assistant saved. A page target, and a
// view the page shows by itself, Curve builder's report among them, open as navigation opens them.
async function showTarget(report:string,selection:Record<string,unknown>|null,kind?:Target['kind']){
  const next=webPage(report);
  if(!selection)return showPage(next);
  // What a target names is checked before the page changes, so a target the page cannot show leaves it as it was.
  switch(report){
    case 'custom_curves':{
      if(kind!=='proposal'&&kind!=='draft')return showPage('custom-curves');
      const scope=()=>JSON.stringify([marketSelection(),historicalContext()?.contextId]);
      const requested=scope(),editor=customCurves.guard();
      if(kind==='draft'){
        const draft=await api<{id:string;version:number}>('assistant/curve-draft',selection);
        if(requested!==scope())throw Error('The account or financial context changed. Open the saved draft again.');
        await setPage('custom-curves');await customCurves.load();
        if(requested!==scope())throw Error('The account or financial context changed. Open the saved draft again.');
        customCurves.openSaved(draft,editor);return;
      }
      const proposal=await api<Parameters<typeof customCurves.openPrepared>[0]>('assistant/curve-proposal',selection);
      if(requested!==scope())throw Error('The account or financial context changed. Review the proposed curve again.');
      await setPage('custom-curves');await customCurves.load();
      if(requested!==scope())throw Error('The account or financial context changed. Review the proposed curve again.');
      customCurves.openPrepared(proposal,editor);return;
    }
    case 'curves':{
      const code=String(selection.curve);
      if(selection.view!=='curves'&&!directory().curves.some(c=>c.code===code))throw Error('That curve is not in this market data.');
      await setPage('market');
      if(selection.view==='curves')return selectCurve('');
      prefs.tab=selection.view==='observations'?'inputs':prefs.tab==='inputs'?'prices':prefs.tab;persist();
      return selectCurve(code);
    }
    case 'reference_data':{
      const table=String(selection.table),search=String(selection.search);await loadWorkspace();
      if(!referenceSections().some(s=>s.value===table))throw Error('That reference table is not available here.');
      await setPage('reference');await showReference(table,search);return;
    }
    // A job's history opens its details on the page of the directory that lists it, found before the page changes.
    case 'jobs':{const job=await jobs.locateJob(String(selection.job));await setPage('jobs');jobs.showJob(job);return;}
    case 'parties':{
      const kind=String(selection.kind);
      if(![...select('party-kind').options].some(o=>o.value===kind))throw Error('That kind of party is not one this page shows.');
      await setPage('parties');select('party-kind').value=kind;await parties.load();return;
    }
    case 'blotter':
      if(selection.view==='trade')futures.requireTradeRows();
      await setPage('futures');futures.reset('read');
      if(selection.view==='trade'){
        if(select('snapshot').value)await futures.load();
        await futures.openTrade(String(selection.tradeId),selection.instrumentType as 'future'|'financial_swap'|'heat_rate'|'ppa',selection.revision as number|null);
      }else await futures.show(selection as Parameters<typeof futures.show>[0]);
      return;
    default:return showPage(next);
  }
}
// Opens Curves & prices on a curve, and one of its contracts in the panel when a contract is named. A contract the close
// does not price leaves only the curve, and says so.
async function openCurve(code:string,contractId:string|null){
  await showTarget('curves',{view:'prices',curve:code});
  if(contractId===null)return;
  if(!result){notice("Choose a market date to see this contract's price history.");return;}
  const point=result.points.find(p=>(p.seriesId??p.contractId)===contractId);
  if(!point){notice('That contract has no price at this close, so its price history is not shown.');return;}
  await openContract(pointIdentity(point));
}
// Selects knowledge and an As of frame, as a target pins them or a place showed them, before a page opens there. Working data's time
// controls then show the frame's effective date at current knowledge, as returning to working data leaves them (D217).
async function selectContext(knowledge:TargetContext|{mode:'current'|'retained';contextId:string|null;frameId:string|null}){
  if(knowledge.mode==='current'){if(contextDirty||historicalContext())await currentInputs();}
  else if(contextDirty||historicalContext()?.contextId!==knowledge.contextId)await selectRetained(knowledge.contextId!);
  const frame=knowledge.frameId??'';
  if(select('snapshot').value!==frame){
    if(frame&&!offeredFrames().some(f=>f.id===frame))throw Error('That As of date is not available in this session. Choose an As of date, or ask again.');
    renderFrames(frame);
    closeChanged();
  }
  if(knowledge.mode==='current')await initializeContext();
}
// A historical view selected again by its id, exactly as it was captured.
async function selectRetained(contextId:string){
  jobs.contextChanged();customCurves.contextChanged();intervalSeries.reset();intervalCurves.reset();parties.reset();unitConverter.reset();futures.reset('inputs');power.reset();
  const snapshot=await api<{context:Context}>('market/context/retained',{contextId,selection:'latest_correction'});
  if(snapshot.context.contextId!==contextId)throw Error('The historical view could not be selected exactly. Reload the page.');
  await viewContext(snapshot);
}
// Back restores the place a move left: its knowledge and frame, then its page and what that showed.
async function restorePlace(origin:Place){
  const c=origin.context;
  if(c.mode!=='unapplied')await selectContext(c.mode==='current'?{mode:'current',contextId:null,frameId:c.frameId}:{mode:'retained',contextId:c.contextId,frameId:c.frameId});
  if(readPages.includes(origin.report.page))await showTarget(origin.report.page,origin.report.selection);else await showPage(origin.page);
}
function tableName(code:string){
  const section=workspace?dataSections(Object.keys(workspace.references)).flatMap(g=>g.sections).find(s=>s.value===code):undefined;
  return section?.name??friendly(code);
}
// The assistant panel, for a person who uses agents (docs/ai-assistant.md §10.1).
$('assistant-hide').onclick=()=>{prefs.assistantHidden=true;persist();assistant.sync();};
const assistant=setupAssistant({launcherHidden:()=>prefs.assistantHidden,api,root:apiRoot,identity:()=>administrationScope+':'+currentProfile,zone:()=>prefs.timeZone,
  available:()=>can('UseAgents'),pageBusy:()=>busy,act,place,names:()=>({curve:curveName,table:tableName}),
  trade:()=>futures.report().trade?.reference,unsaved,open:target=>showTarget(target.page,target.selection,target.kind),
  // A proposed curve or a saved draft opens under the header the page shows (D260).
  go:async target=>{if(target.kind!=='proposal'&&target.kind!=='draft')await selectContext(target.context);await showTarget(target.page,target.selection,target.kind);},restore:restorePlace});
// Coming back to the window asks whether a newer close arrived while it was away.
window.addEventListener('focus',()=>void recheckNewestClose());
// A reload, closing the tab and going back leave the page without the panel's own question, so the browser asks while the panel holds an
// amendment with edits, or a custom curve holds unsaved ones: the listener is on while it does, and off once the edits are saved or given up. A session that has ended cannot
// send what the form holds, and is not asked about.
const askBeforeUnload=(event:BeforeUnloadEvent)=>{if(sessionEnded)return;event.preventDefault();event.returnValue='';};
let askingBeforeUnload=false;
function syncUnloadQuestion(){
  const holds=futures.editing()||customCurves.editing();
  if(holds===askingBeforeUnload)return;
  askingBeforeUnload=holds;
  if(holds)window.addEventListener('beforeunload',askBeforeUnload);else window.removeEventListener('beforeunload',askBeforeUnload);
}
let initialWorkspaceReady=false;
void act(async()=>{
const [examples,boot]=await Promise.all([transport<{id:string;name:string;url:string}[]>('examples',undefined,'/api/'),api<ProfileChoices & {csrfToken:string;profile:string|null;runId:string;tenantId:string;workspaceId:string;access?:StartupAccess|null}>(startupBootstrapPath)]);
const chooser=select('example-dataset');chooser.replaceChildren(...examples.map(e=>new Option(e.name,e.id)));chooser.value=exampleId;chooser.onchange=()=>void (async()=>{
  const selected=examples.find(e=>e.id===chooser.value);
  if(!selected)return;
  // Another example is another page load, which gives up what the panel holds unless the person keeps it: the select then shows the example the page is.
  if(holdsEdits()&&(busy||!await mayLeave())){chooser.value=exampleId;return;}
  location.assign(selected.url);
})();
csrf=boot.csrfToken;administrationScope=[boot.runId,boot.tenantId,boot.workspaceId].join(':');setHostMode(boot.mode);
  // Synthetic sign-in is an explicit loopback preview facility only.
  $('example').hidden=hostedPilot;if(hostedPilot&&!boot.profile)throw Error('The signed-in account could not be verified.');
  if(boot.profile===null?boot.access!==null:typeof boot.profile!=='string'||!boot.profile||boot.access===undefined||boot.access===null)throw Error('The workspace access metadata is missing or inconsistent. Refresh and try again.');
  currentProfile=boot.profile??'viewer';if(boot.profile===null)await api('profile',{profile:currentProfile});await loadIdentity(boot.profile===null?undefined:boot.access);await profileChoices(boot);initialWorkspaceReady=true;
}).then(async()=>{
  if(!initialWorkspaceReady)return;
  await new Promise<void>(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve())));
  if(!busy&&$('error').hidden)performance.mark('leanetrm:workspace-ready');
});
