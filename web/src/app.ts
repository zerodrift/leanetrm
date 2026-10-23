import {setupPpa} from './ppa-editor.js';
import {setupCustomCurves} from './custom-curves-view.js';
import {LiveUpdates,liveContextUpdate,type LiveSelection,type LiveContextUpdate,type LiveStatus} from './live-context.js';
import {setupMarketCalendar,type MarketDates} from './market-calendar.js';
import {newestDate,newestClose,closeBadge,type CloseChoice} from './latest-close.js';
import {setupPowerImports} from './power-imports-view.js';
import {setupIntervalSeries} from './interval-series-view.js';
import {setupIntervalCurves} from './interval-curves-view.js';
import {setupJobs} from './jobs-view.js';
import {setupReferenceEditor} from './reference-editor.js';
import {setupPartyEditors} from './party-view.js';
import {amountText,prepareAmountDisplay} from './amount-display.js';
import {setupUnitConverter} from './units-view.js';
import {renderChart,clearChart} from './chart-view.js';
import {setupFutures} from './futures-view.js';
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
import {type Row,type Frame,type Workspace,type CurveResult,type PriceHistory,type ReferenceDisplay,displayReferenceRows,presentedReferences,val,publicText,friendly,curveName,setCurveNames,exactRow,commodityName,displayInstant,deliveryMonth,frameOptions,quoteUnit,publicColumns,timeLabels,marketTypeName,allCurves,displayDate,pointDate,pointIdentity,deliveryLabel,historyPriceLabel} from './market-view.js';
import {dataSections} from './data-sections.js';
import {type Preferences,newPreferences,loadPreferences,savePreferences,validTimeZone,zoneChoices} from './preferences.js';
import {setupShortcutEditor,scheduleErrorMessage} from './shortcut-view.js';
import {setupTouProducts} from './tou-products.js';
import {validateMarketNavigation,navigationGroups,navigationCategories,navigationCurveKind,type MarketNavigation,type FrameSummary} from './market-navigation.js';
import {navigationGroups as sidebarGroups,pageGroup,pageAvailable,firstPage} from './navigation-groups.js';
import {setupAssistant} from './assistant-panel.js';
import {pageReport,webPage,type PageState,type PageContext,type Place} from './assistant-pages.js';
import {readPages,type TargetContext} from './assistant-events.js';

const exampleId=new URLSearchParams(location.search).get('example')??'baseline';
if(!['baseline','exchange'].includes(exampleId))throw Error('Choose an available example.');
const apiRoot=exampleId==='exchange'?'/examples/exchange/api/':'/api/';
const $=<T extends HTMLElement=HTMLElement>(id:string):T=>{const node=document.getElementById(id);if(!node)throw Error('This page could not load. Please refresh.');return node as T;};
const select=(id:string)=>$<HTMLSelectElement>(id);
const groupNode=(id:string)=>$<HTMLDetailsElement>('nav-group-'+id);
const text=(id:string,value:string)=>{$(id).textContent=publicText(value);};
function el<K extends keyof HTMLElementTagNameMap>(tag:K,content?:string){const node=document.createElement(tag);if(content!==undefined)node.textContent=publicText(content);return node;}
function choose(id:string,options:{value:string;name:string}[],value:string){const node=select(id);node.replaceChildren(...options.map(o=>{const option=el('option',o.name);option.value=o.value;return option;}));node.value=value;}
let csrf='',workspace:Workspace|undefined,curve='',result:CurveResult|undefined,contract='',historyData:PriceHistory|undefined,pricingContract='',showAllContracts=false,input='',draftFor='',busy=false,page='market';
let sourceOnlyCurve=false,historyRequested=false;
let contextDirty=false,currentProfile='',administrationScope='',permissions:string[]=[];
let navigation:MarketNavigation|undefined,marketGeneration=0;
let liveSucceeded:Date|undefined;
let liveStream:EventSource|undefined,liveUpdates:LiveUpdates<LiveContextUpdate & {generation:number}>|undefined,liveId='',lastLiveUpdate:LiveContextUpdate|undefined;
type ReferencePage={table:string;rows:Row[];references:Record<string,Row[]>;referenceDisplay?:ReferenceDisplay|null;offset:number;pageSize:number;maximumOffset:number;hasMore:boolean;search:string;referenceRevision:{catalogId:string;revisionId:string};definitionRevisionId:string;context:Context|null;profile:string;referenceScope:'selected_table'};
let referencePage:ReferencePage|undefined;
const canAdvanceReference=()=>!!referencePage&&referencePage.hasMore&&referencePage.offset+referencePage.pageSize<=referencePage.maximumOffset;
let hostedPilot=false;
type ProfileChoices={mode:string;profiles:{id:string;name:string}[];customProfiles:{id:string;name:string}[]};
type StartupAccess={profile:string;permissions:string[]};
const can=(permission:string)=>permissions.includes(permission);
const historicalContext=()=>navigation?.context??undefined;
const canCorrect=()=>can('ImportObservations')&&!historicalContext();
let prefs:Preferences=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);
let marketDateGeneration=0,marketDatesLoaded=false;
// The workspace's newest close (D212): the last availability read for the whole workspace, the newest date it had when
// the close now shown was selected, whether a newer-close check is due and whether one is running.
let workspaceCloses:MarketDates|undefined,closeBasis:{close:string;newest:string}|undefined,closeCheckDue=false,checkingCloses=false;
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
function notice(message:string){text('notice',message);$('notice').hidden=false;}
function persist(){try{savePreferences(localStorage,(exampleId==='baseline'?currentProfile:exampleId+':'+currentProfile),prefs);}catch{notice('Your changes work for this visit, but this browser could not save your preferences.');}}
function loadProfile(){clearTableFilters();try{prefs=loadPreferences(localStorage,(exampleId==='baseline'?currentProfile:exampleId+':'+currentProfile),Intl.DateTimeFormat().resolvedOptions().timeZone);}catch{prefs=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);notice('Your saved profile could not be loaded. This visit uses your browser’s time zone. You can save your preferences in Profile.');}}
function syncControls(){
  renderWorkspaceMode();
  document.querySelectorAll<HTMLButtonElement|HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>('button,input,select,textarea').forEach(n=>n.disabled=busy);
  if(!busy){$('save').toggleAttribute('disabled',!canCorrect());select('snapshot').disabled=['admin','ai'].includes(page);$('follow-live').toggleAttribute('disabled',!select('snapshot').value);}
  for(const id of ['source-contract','pricing-contract'])select(id).disabled=busy||showAllContracts||!result?.points.length;
  for(const id of ['source-all','pricing-all'])$<HTMLButtonElement>(id).disabled=busy||!result||!result.points.length||(result.points.length<2&&!showAllContracts);
  $('reference-previous').toggleAttribute('disabled',busy||!referencePage||referencePage.offset===0);$('reference-next').toggleAttribute('disabled',busy||!canAdvanceReference());
  futures.sync();parties.sync();agentGrants.sync();jobs.sync();aiSettings.sync();referenceEditor.sync();dateEditor.sync();deliveryPrices.sync();intervalSeries.sync();intervalCurves.sync();ppa.sync();marketCalendar.sync();renderCloseBadge();if(initialWorkspaceReady)customCurves.sync();assistant.sync();document.body.setAttribute('aria-busy',String(busy));
}
function renderWorkspaceMode(){
  const historical=!!historicalContext();
  text('workspace-mode-label',contextDirty?'Unapplied historical selection':navigation?.live?'Live corrections · read only':historical?'Historical view · read only':'Working data · current knowledge');
  $('return-working').hidden=!historical&&!contextDirty;
  $('follow-live').hidden=historical||contextDirty||!['market','custom-curves','futures'].includes(page);
  $('follow-live').toggleAttribute('disabled',busy||!select('snapshot').value);
  const today=new Intl.DateTimeFormat('en',{timeZone:prefs.timeZone,year:'numeric',month:'short',day:'numeric'}).format(new Date());
  text('workspace-today',`Today: ${today} · ${prefs.timeZone}. The As of selector shows available prices; working data can have an earlier market date.`);
}
async function transport<T>(path:string,body?:unknown,root=apiRoot):Promise<T>{
  let response:Response;
  try{response=await fetch(root+path,{method:body===undefined?'GET':'POST',credentials:'same-origin',headers:body===undefined?{}:{'Content-Type':'application/json','X-Preview-CSRF':csrf},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(20000)});}
  catch{throw Object.assign(Error(body!==undefined?'We could not confirm the result. Refresh and check the saved changes before saving again.':'The app could not connect. Please refresh the page.'),{code:'response_unknown'});}
  if(response.status===401){workspace=undefined;result=undefined;historyData=undefined;curve='';document.body.hidden=true;location.replace('/auth');throw Object.assign(Error('Sign in to continue.'),{code:'sign_in_required'});}
  if(!response.ok){const error=await response.json().catch(()=>({code:'request_failed'}));if(path.startsWith('ppa/'))throw Object.assign(Error(`${error.code??'request_failed'}: ${error.message??'The PPA operation was refused.'}`),{code:error.code,details:error});const messages:Record<string,string>={index_fixing_convention_missing:'This index has no qualified fixing schedule for the selected observations. Its reference rules must be configured before this swap can be priced.',index_fixing_missing:'A published index fixing is required but is missing from the selected market inputs. Load the fixing and select a complete market context.',index_fixing_definition:'The fixing schedule does not match the exact index, price units or source definition.',swap_fixing_read_only:'Fixing dates come from the selected index convention and cannot be entered manually.',reference_edit:'Choose a reference row and complete its supported display fields. Financial terms are read-only.',reference_conflict:'This reference version changed. Reload before editing or reconcile the retained request.',reference_limit:'This reference catalog exceeds the supported metadata authoring limits.',reference_economic_change:'The saved version changes financial terms and cannot be applied as a display update.',reference_outcome_unknown:'The save outcome is unconfirmed. Inspect versions or retry the retained original request.',missing_curve_view:'This curve has no configured close for the selected date. Choose an available calendar date.',missing_observation:'Required market data is missing for this curve and close. Choose another available date or load the missing prices.',recipe_definition:'Complete every required operation setting and connect each node to the curve output with compatible inputs.',recipe_input:'The selected dates or contracts do not have complete valid observations. Check the source and date window.',recipe_transform:'This saved curve uses an operation that is unavailable in the connected engine. Recreate it with supported graph operations.',recipe_formula:'Use quoted parameters, connected @node inputs and typed JSON contract mappings.',custom_curve_definition:'Complete the curve operations, connections and required settings. Check the selected sources and date coverage.',custom_curve_conflict:'This curve changed or the code is already used. Reload the library before editing.',custom_curve_coverage:'The selected dates or contracts do not have complete observations. Choose a covered window or load the required prices.',custom_curve_operator:'This curve uses an unsupported operation or incompatible input connection.',custom_curve_source:'Choose an available curve from the selected market context.',custom_curve_limit:'This curve exceeds the supported graph or history bounds.',futures_execution_policy:'Trading eligibility is missing, ambiguous or inconsistent with its source. A maintainer must qualify this contract before it can be priced.',unit_catalog:'The configured unit catalog is unavailable at this effective date or knowledge cutoff.',reference_catalog:'The configured reference catalog is unavailable in this context.',reference_page:'Choose an available category and valid page or search text.',clock_tzdb:'The installed time-zone rules differ from the product clock’s declared release, or cannot be verified. Calculations are blocked until they match.',clock_rule:'This product’s market clock is incomplete or unknown in reference data.',power_calendar:'The settlement index calendar does not cover this whole delivery period.',power_index:'This curve needs a single settlement index with matching commodity, currency and unit.',tou_definition:'Supply a lowercase code, a name, a market clock and an ordered peak window.',tou_conflict:'This TOU product changed or its code is already used. Reload before editing.',clock_reference:'Choose a market clock from this workspace’s reference data.',calendar_reference:'Choose a business calendar from this workspace’s reference data.',calendar_binding:'Choose a reference calendar, not a typed one.',futures_revision_conflict:'This trade changed. Reload the current revision before amending.',futures_amendment:'Supply an explicit amendment reason and use the selected working As of time.',futures_no_change:'Change a supported booking term before previewing an amendment.',futures_trade:'The trade revision is unavailable in this context.',historical_read_only:'This historical view is read-only. Return to working data before booking or amending.',risk_stress:'Choose signed nonzero curve-coordinate shifts using the supported additive convention, with at most sixteen nested stresses.',scenario_target:'Choose an available exact curve, contract or point target; overlapping shifts belong in separate nested layers.',scenario_coordinate:'Choose a primitive quote for input shocks or a computed point for output shifts. Frozen observations cannot be shocked.',scenario_bucket:'Choose an ordered delivery window whose boundaries do not split a contract or computed point.',scenario_shift:'Choose an offered quote or output target, its exact unit and a valid signed shift within the supported nesting limit.',risk_context:'Select the shared financial context before running risk.',risk_job_context:'Queue the exact retained financial context with a stable request identity.',risk_job_conflict:'This request identity belongs to different risk inputs. Inspect the retained job before submitting a new request.',risk_job_outcome_unknown:'The job submission outcome is unknown. Reconcile the retained submission to retry the same request safely.',risk_engine_version:'This retained job requires its original managed engine version.',risk_timeout:'The risk job exceeded its configured calculation time limit.',risk_input_timeout:'The retained risk inputs could not be loaded within the configured time limit.',risk_result_limit:'The risk result exceeds the retained report size limit. Narrow the selected books or request book totals.',risk_bump:'Enter a positive absolute bump and choose distinct supported scenario targets.',risk_factor:'Choose an available quote or computed-output target in this market context.',risk_position:'Choose books containing live supported holdings.',risk_policy:'Choose the supported exact repricing policy.',risk_convention:'Choose the supported repricing convention for this risk operation.',portfolio_policy:'Refresh the risk catalog to select its complete supported pricing policies.',portfolio_context:'Select a complete retained financial context covering the held instruments and their market inputs.',swap_risk_model:'A held swap requires market inputs that are unavailable in this exact financial context.',precision_loss:'An exact result needs more precision. For a conversion, choose explicit final rounding; no value was rounded automatically.',price_domain:'A base or shocked price is outside the contract’s allowed price range.',table_view_conflict:'This saved view changed in another session. Use Reload views before saving or deleting it.',table_view:'Choose a valid named layout; at most twenty views can be saved per table.',blotter_view:'Choose available books and explicit pivot dimensions/measures. Lots require dated-contract grouping.',power_historical:'Return to current inputs to use the unsaved power example. Historical clock publication is not supported yet.',power_units:'This example needs an outright curve quoted per MWh.',settlement_coverage:'The selected mask has no intervals or the fixing coverage is incomplete.',power_input:'Complete the product, units and classification choices.',access_denied:hostedPilot?'Your account does not have permission for that operation.':'This demo user can’t make that change. You can switch users in Profile → Preview access.',revision_conflict:'This price has already changed. Refresh and choose the version you want in Price changes.',mutation_outcome_unknown:'We could not confirm whether your change was saved. Refresh and check the saved changes before saving again.',numeric_format:'Enter a number with no commas or spaces, using up to 18 decimal places.',missing_value:'Enter a new value before saving.',definition_rejected:'This value does not meet the contract’s price rules. Check the allowed range and settlement increment in Reference data.',snapshot_missing:'Choose an available market date.',request_token:'Please refresh the page before making this change.',request_cancelled:'This took too long. Please refresh and try again.',admin_conflict:'This account changed or the login name is already registered. Refresh accounts before editing.',last_administrator:'Keep at least one current administrator in this workspace.',self_access_change:'Another administrator must remove your administrative access.',admin_input:'Enter a name, login, identity kind and explicit roles.',admin_limit:'This preview has reached its account limit.',administration_outcome_unknown:'We could not confirm the change. Check accounts and recent access changes, then retry the retained request if necessary.',model_admin_input:entryRanges,model_admin_conflict:'This provider or model changed, the model already exists, its current state does not allow the change, or the request identity was used for a different change. Refresh AI settings before trying again.',model_admin_outcome_unknown:'We could not confirm the change. Check the current AI settings and recent changes, then retry the retained request if necessary.',model_identifier_invalid:'Enter the vendor’s model identifier: up to 128 letters, digits, dots, underscores or hyphens, starting with a letter or digit.',currency_unavailable:'Choose a currency of a sealed reference revision that your roles can currently read.',model_unqualified:'Enable a model once its capabilities are recorded under the usage mapping its vendor’s adapter applies.',model_provider_unqualified:'The provider is not qualified: this release must confirm its usage mapping, and its current key and terms must be attested.',model_provider_disabled:'The model’s provider is disabled. Enable the provider first.',model_mapping_unconfirmed:'Record capabilities once this release confirms a usage mapping for the model’s vendor.',model_unpriced:'The model needs a rate set in force now.',model_changed:'The model’s capability version changed. Refresh AI settings and choose the version listed now.',model_disabled:'The model is disabled. Enable it before an agent version names it.',model_limit_exceeded:'Ask for no more output tokens per call than the model version allows.',grant_duration_exceeded:'The grant is longer than the limit stated with it.',instructions_too_long:'The instructions are longer than the limit stated with them.',agent_retired:'This agent is retired and can no longer change.',parameter_required:'Choose one of the reasoning efforts the model’s capability version records.',test_in_progress:'This test has no recorded outcome yet. Retry the retained request to read it once it is recorded.',report_zone_invalid:'Choose a time zone the preview’s database knows, named exactly as it names it.',report_range_invalid:'Enter a From date before the To date, each written yyyy-MM-dd, no further apart than the Usage tab states.',agent_runtime_unavailable:'Tests run only while this process holds the agent worker, which another process may hold or whose connection was lost. Try again shortly.',credential_unavailable:'Key storage is unavailable here, so no key can be saved or opened. Its key files are missing or unreadable: ask whoever runs this installation to restore them.',agent_managed:'Agents can’t be created or changed here, and only agents hold the agent reader role.',numeric_range:'A number is too large: its magnitude must be below 10^24.',interval_series_resolution:'Choose a 5, 15, 30 or 60 minute resolution.',interval_series_empty:'Add at least one value to import.',interval_series_size:'A revision holds at most 200,000 intervals, so split a larger import into several smaller ones.',interval_series_instant:'Write each start as a UTC instant, for example 2027-01-01T05:00:00Z, within the years 2000 to 2099.',interval_series_grid:'Every start must be where one of the series’ intervals starts on its market clock, and From and To may not fall inside a stored interval.',interval_series_overlap:'Intervals within one import cannot overlap or repeat.',interval_series_historical:'Return to working data to edit or read interval series, then use Known at on this page to see earlier knowledge.',interval_curve_historical:'Return to working data to read interval curves; their TOU and shape inputs are current definitions.',interval_curve_window:'Choose From and To on the curve’s interval grid, at most 35,136 intervals apart.',interval_curve_cutoff:'Choose Known at for a shaped curve or when resolving actuals.',interval_contract:'Choose a curve whose constituents are exact delivery intervals on its market clock.',interval_block:'A block curve must be date-only and bound to a published TOU peak window, or cover all hours.',interval_block_partition:'The curve’s block marks must cover every interval in this window exactly once. Choose a window inside its marked months.',interval_shape:'The shape cannot calibrate a block: multiplicative factors need a positive weighted mean under the supported method.',interval_shape_coverage:'The shape has no value for an interval of a block this window touches at the chosen Known at. Choose a later Known at or complete the shape.',interval_actual:'This curve has no collected actual-price source and due rule, or its collected prices do not match its intervals.',interval_actual_missing:'A delivered interval’s actual price is due but was not collected by the chosen Known at.',interval_aggregate:'Aggregate resolved prices over whole local hours or operating days, with From and To on their boundaries.',interval_curve_coverage:'The curve has no interval for part of this window. Choose From and To within its intervals.',interval_delivered:'This window includes intervals delivered by the market date. Choose Resolve to price them from collected actuals.',interval_series_conflict:'This request was already used for a different change, so start a new request.',interval_series_revision_conflict:'Someone else saved this series first, so reload it and apply your change again.',interval_series_values:'Check the values: each must be a plain decimal with at most 18 decimal places inside the series’ allowed range, and no interval may overlap a stored one that starts at a different time.',interval_series_window:'Choose a From before To spanning at most 35,136 intervals and 100,000 changes, with a Known at no later than now.',interval_series_code:'Choose an unused code that starts with a lowercase letter and uses lowercase letters, digits, underscores, dots or hyphens.',interval_series_definition:'Name the series in 1 to 80 characters without leading or trailing spaces, and choose a kind, specification and clock that belong together in this workspace’s reference data.',interval_series_request:'This request has no identity, so refresh the page and try again.',interval_series_reason:'Give a reason of 1 to 500 characters that is not blank.',interval_series_outcome_unknown:'We could not confirm whether the save completed, so retry the original request to recover its receipt or check the change log first.',request_too_large:'The request is too large for this operation; for an import, split it into smaller ones.'};throw Object.assign(Error(agentRefusal(error)??scheduleErrorMessage(error.code)??messages[error.code]??'The app could not complete that request. Please refresh and try again.'),{code:error.code});}
  return response.json() as Promise<T>;
}
async function api<T>(path:string,body?:unknown):Promise<T>{
 const payload=await transport<T>(path,body);
 if(['profile','market/current','market/context'].includes(path))stopLive();
 if(/^(trading\/(blotter|workspace)|swaps\/(preview|editor)|futures\/(holdings|editor|amend-preview)|evaluate|history|power\/preview)$/.test(path)){
  const displayed=path==='trading/workspace'?(payload as {blotter:unknown}).blotter:path==='evaluate'||path==='history'?{points:(payload as CurveResult|PriceHistory).points}:payload;
  await prepareAmountDisplay(displayed,request=>transport<string[]>('amounts/display',request),path==='history'?decimalTick():undefined);
 }
 return payload;
}
function pageStatus(){return page==='ppa'?'PPA bookings · exact selected inputs and certified knowledge.':page==='interval-series'?'Interval series · working data, known at the chosen time.':page==='interval-curves'?'Interval curves · marks from the selected market date; shapes and actuals known at the chosen time.':page==='custom-curves'?'Custom curves · selected market context.':page==='jobs'?'Jobs and infrastructure · current authority.':page==='futures'?'Trading blotter · saved market inputs.':page==='admin'?'Workspace identity and access administration.':page==='ai'?'Page-assistant settings · current authority.':page==='schedules'?'Reusable schedule shortcuts.':result?`Prices for ${instant(result.at)}`:select('snapshot').value?'Choose a curve to get started.':'Choose a curve and a market date to get started.';}
const errorMessage=(error:unknown)=>error instanceof Error?error.message:'Something went wrong. Please refresh the page.';
async function act(work:()=>Promise<void>){
  // Context changes, writes and reads are serialized. Never retry a mutation.
  // Errors and notices describe the previous action; the next one starts clean.
  if(busy)return;busy=true;syncControls();$('error').hidden=true;$('notice').hidden=true;text('status','Loading…');
  try{await work();text('status',pageStatus());}
  catch(error){text('error',errorMessage(error));$('error').hidden=false;text('status','Please check the message above.');}
  finally{busy=false;syncControls();if(closeCheckDue){closeCheckDue=false;void recheckNewestClose();}}
}
function action(name:string,run:()=>Promise<void>){const button=el('button',name);button.disabled=busy;button.type='button';button.onclick=()=>void act(run);return button;}
function decimalTick(){if(!workspace||!curve||listedCurve().marketType!=='futures')return undefined;return val(row('quote_specifications','code',val(selectedCurve(),'quote_code'),true),'clearing_increment');}
function table(target:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]=[],displayRows?:string[][]){
  const referenceScope=[data().profile,page,select('reference-table').value,referencePage?.referenceRevision,referencePage?.offset,referencePage?.search,historicalContext()];
  const marketScope=[data().profile,curve,result?.snapshotId??'',target==='history-values'?contract:'',target==='revisions'?input:'',...(['inputs','mappings'].includes(target)?[showAllContracts||target==='inputs'&&sourceOnlyCurve?'all':pricingContract]:[])];
  renderRegularTable($(target),target,JSON.stringify(target==='reference'?referenceScope:marketScope),headers,rows,numeric,()=>busy,{displayRows});
}

function clearPrices(){historyRequested=false;result=undefined;pricingContract='';contract='';historyData=undefined;$('history-card').hidden=true;clearChart($('history-chart'));input='';draftFor='';$('revision-card').hidden=true;text('build-info','');text('price-date','Choose a market date to view prices.');if(curve)renderSummary();clearChart($('curve-chart'),'Choose a market date above.');$('curve-chart').classList.add('empty');$('contracts').replaceChildren();$('inputs').replaceChildren();if(curve)renderDependencies();}
const showsDate=(next:string)=>!['admin','ai','jobs','interval-series','ppa'].includes(next);
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
  page=next;$('ppa-page').hidden=next!=='ppa';$('custom-curves-page').hidden=next!=='custom-curves';$('jobs-page').hidden=next!=='jobs';$('delivery-page').hidden=next!=='delivery';$('interval-series-page').hidden=next!=='interval-series';$('interval-curves-page').hidden=next!=='interval-curves';$('parties-page').hidden=next!=='parties';$('futures-page').hidden=next!=='futures';$('power-page').hidden=next!=='power';select('snapshot').closest<HTMLElement>('.date-control')!.hidden=!showsDate(next);$('market-page').hidden=next!=='market';$('reference-page').hidden=next!=='reference';$('schedules-page').hidden=next!=='schedules';$('curve-date-editor').hidden=next!=='configuration';$('date-context').hidden=!showsDate(next);$('admin-page').hidden=next!=='admin';$('ai-page').hidden=next!=='ai';$('no-access-page').hidden=true;
  text('page-section',pageGroup(next).toUpperCase());text('header-calculation-time','');text('page-title',next==='jobs'?'Jobs & Infrastructure':next==='custom-curves'?'Custom curves':next==='delivery'?'Delivery prices':next==='interval-series'?'Interval series':next==='interval-curves'?'Interval curves':next==='parties'?'Entities & counterparties':next==='ppa'?'PPA bookings':next==='futures'?'Blotter':next==='power'?'Power TOUs':next==='admin'?'Users & access':next==='ai'?'AI':next==='market'?'Curves & prices':next==='configuration'?'Contract definitions':next==='schedules'?'Schedules':'Reference data');
  for(const name of ['market','custom-curves','delivery','interval-series','interval-curves','power','reference','configuration','schedules','admin','ai','futures','ppa','parties','jobs'])$('nav-'+name).classList.toggle('active',next===name);
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
 if(catalog.selectionPolicy!=='exact_snapshot_no_close_substitution_v1'||catalog.timeZone!==prefs.timeZone||catalog.curve!==request.curve||
  JSON.stringify(catalog.context??null)!==JSON.stringify(historicalContext()??null)||new Set(frames.map(close=>close.id)).size!==frames.length||
  catalog.dates.some(group=>!/^\d{4}-\d{2}-\d{2}$/.test(group.date)||!group.frames.length))
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
  marketDatesLoaded=true;marketCalendar.update(catalog);marketCalendar.show(date);
 }catch(error){
  if((error as {code?:string}).code==='access_denied'){clearPrices();if(page==='custom-curves')customCurves.contextChanged();}
  marketCalendar.unavailable(page==='custom-curves'?'Complete a valid curve graph with available inputs to see its closes.':'Market-date availability could not be verified. Open the calendar to retry.');
  if(page==='custom-curves'&&['recipe_definition','custom_curve_definition','custom_curve_source','recipe_formula'].includes((error as {code?:string}).code??'')){marketCalendar.show();return;}
  throw error;
 }
}
// Opening a workspace on its newest close (D212). The workspace-wide calendar names the newest date: one close on it is
// selected as if the person had picked it, several ask, and none selects nothing. The page that is showing then reads it.
const workspaceDates=():MarketDatesRequest=>({curve:null,timeZone:prefs.timeZone,definition:null});
async function selectNewestClose():Promise<CloseChoice>{
 const catalog=await readMarketDates(workspaceDates());
 if(!catalog)return {kind:'none'};
 const choice=newestClose(catalog);
 workspaceCloses=catalog;
 if(choice.kind==='single'){select('snapshot').value=choice.closeId;closeChanged();}
 return choice;
}
// A click on Newer close available. Selecting it loads the page like a close the person picked.
async function openNewestClose(){
 const choice=await selectNewestClose();
 if(choice.kind==='single')await loadSelectedClose();
 else if(choice.kind==='choose')await openMarketCalendar(choice.date);
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
// The badge compares the close shown with the workspace's newest. Its basis is the newest date known when that close was
// selected (taken the first time the close is rendered, and again whenever it is selected), so a later date is news
// whoever selected the close. A historical or unapplied view has no newest close to claim, and a workspace without closes
// has nothing to compare.
function renderCloseBadge(){
 const closes=workspaceCloses,shown=select('snapshot').value,newest=closes?newestDate(closes):null;
 if(newest&&closeBasis?.close!==shown)closeBasis={close:shown,newest};
 marketCalendar.badge(closes&&newest&&closeBasis&&!contextDirty&&!historicalContext()?closeBadge(shown,closeBasis.newest,closes):null);
}
// The As of dates the selector offers: a historical view's own date's frames, or every frame of working data.
function offeredFrames(){const context=historicalContext();return context?directory().frames.filter(f=>f.at.slice(0,10)===context.effectiveOn):directory().frames;}
function renderFrames(value:string){const frames=offeredFrames();choose('snapshot',[{value:'',name:frames.length?'Choose a date…':'No inputs for this context date'},...frameOptions(frames,prefs.timeZone)],value);select('snapshot').title=`Times shown in ${prefs.timeZone}`;invalidateMarketDates();}
function renderBrowse(){
  const categories=navigationCategories(directory()),commodities=categories.map(c=>c.code);
  // No commodity code is empty, so the empty value can only mean All markets.
  choose('commodity',[{value:'',name:'All markets'},...categories.map(c=>({value:c.code,name:c.name}))],prefs.market??'');
  // Discard an unavailable presentation filter; market inputs remain separately pinned.
  if(prefs.market!==null&&!commodities.includes(prefs.market)){prefs.market=null;select('commodity').value='';}
  select('browse-by').value=prefs.browseBy;document.querySelectorAll<HTMLButtonElement>('[data-type]').forEach(b=>{const active=b.dataset.type===prefs.marketType;b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));});
  const groups=navigationGroups(directory(),prefs.browseBy,prefs.market,prefs.marketType);text('curve-count',String(groups.reduce((n,g)=>n+g.curves.length,0)));
  const owner=directory();
  $('curves').replaceChildren(...groups.map(group=>{const section=el('details'),key=prefs.browseBy+'|'+group.key;section.className='curve-group';section.open=!prefs.collapsedCurveGroups.includes(key);section.append(el('summary',group.name));
    section.ontoggle=()=>{if(!section.isConnected||navigation!==owner)return;const closed=prefs.collapsedCurveGroups.includes(key);if(closed===!section.open)return;prefs.collapsedCurveGroups=section.open?prefs.collapsedCurveGroups.filter(k=>k!==key):[...prefs.collapsedCurveGroups,key].slice(-64);persist();};
    group.curves.forEach(c=>{const button=el('button');button.className='curve-button'+(c.code===curve?' selected':'');button.disabled=busy;button.setAttribute('aria-pressed',String(c.code===curve));button.append(el('span',curveName(c.code)),el('small',marketTypeName(c.marketType)));button.onclick=()=>void act(async()=>{curve=c.code;invalidateMarketDates();clearPrices();if(select('snapshot').value)await build();else{await loadWorkspace();renderContext();}});section.append(button);});return section;}));
  if(!groups.length)$('curves').append(el('p','No curves match these choices.'));
}
function renderContext(){
  const example=directory().example;$('example').hidden=!example;text('example-help',example?`${curveName(example.curve)} · ${instant(example.at)}`:'No market inputs have been imported.');renderLayout();renderBrowse();$('welcome').hidden=!!curve&&!!workspace;$('curve-detail').hidden=!curve||!workspace;
  $('curve-power').hidden=!curve||!workspace?.powerCurveCodes.includes(curve);
  if(curve&&workspace){const c=selectedCurve(),listed=listedCurve(),kind=listed.marketType;renderSummary();text('curve-kind',navigationCurveKind(listed));text('curve-title',curveName(curve));
    const specification=kind==='rates'?row('interest_rate_definitions','code',val(c,'interest_rate_code'),true):undefined;
    text('unit',specification?`${val(row('currencies','code',val(specification,'currency_code'),true),'display_code')} · % per year`:unit(val(c,'quote_code')));
    text('chart-title',kind==='rates'?'Zero-rate curve':kind==='futures'?'Futures curve':kind==='forwards'?'Forward curve':'Mixed curve');
    text('source-title',kind==='rates'?'Rates behind this curve':'Prices behind this curve');text('source-help',kind==='rates'?'Annual zero rates for the listed maturities.':'Prices and conversion factors for the selected market date.');text('history-help',kind==='rates'?'One maturity, followed over time.':'One contract, followed over time.');
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
  const items:{name:string;value:string;numeric?:boolean}[]=[];
  if(result&&curve){
    const rates=result.kind==='rates',front=result.points[0];
    if(front)items.push({name:rates?'Maturities':'Contracts',value:String(result.points.length)},{name:rates?'First maturity':'Front contract',value:rates?displayDate(pointDate(front)):deliveryLabel(front.deliveryFrom!,front.deliveryTo!)},{name:rates?'First zero rate (%)':'Front price',value:front.value,numeric:true});
    else items.push({name:'Contracts',value:'None remaining'});
    items.push(rates?{name:'Day count',value:rateConvention().dayCount}:{name:'Settlement',value:settlementText()});
  }
  $('curve-summary').hidden=!items.length;
  $('curve-summary').replaceChildren(...items.map(item=>{const group=el('div'),value=el('dd',item.value);if(item.numeric)value.className='numeric';group.append(el('dt',item.name),value);return group;}));
  const context=historicalContext(),banner=$('curve-mode');banner.hidden=!curve||contextDirty||(!context&&!canCorrect());
  banner.dataset.tone=context?'historical':'editor';
  text('curve-mode',context?`Historical view: ${displayDate(context.effectiveOn)}${context.knownAt?`, as known at ${instant(context.knownAt)}`:''}. Prices are read-only; return to working data to make changes.`:'You can correct source prices on the Price sources tab. Every earlier version stays available.');
}
function renderPrices(){
  if(!result)return;renderSummary();const rates=result.kind==='rates';if(result.availability==='no_remaining_contracts'){pricingContract='';contract='';historyData=undefined;$('history-card').hidden=true;text('price-date',`As of: ${instant(result.at)}`);text('build-info','No remaining delivery days');clearChart($('curve-chart'),'No remaining delivery days for this market date.');clearChart($('history-chart'));$('contracts').replaceChildren();renderDependencies();renderInputs();return;}
  const exportCurve={snapshotId:result.snapshotId,curve:result.curve};
  renderChart($('curve-chart'),{exportImage:download=>void act(async()=>{try{await api('evaluate',exportCurve);}catch(error){clearPrices();throw error;}download();}),title:curveName(result.curve),axis:rates?'maturity':'delivery',unit:$('unit').textContent??'',series:[{name:curveName(result.curve),points:result.points.map(p=>({x:pointIdentity(p),label:rates?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!),y:p.value}))}]});
  text('price-date',`As of: ${instant(result.at)}`);text('build-info',`${result.points.length} ${rates?(result.points.length===1?'rate sample':'rate samples'):(result.points.length===1?'contract':'contracts')}`);
  table('contracts',rates?['Maturity','Zero rate (%)','Discount factor','']:['Delivery period','Price','Expires',''],result.points.map(p=>[rates?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!),p.value,rates?p.displayDiscountFactor!:instant(p.expiry!),action(rates?'Rate history':'Price history',async()=>{contract=pointIdentity(p);await history();})]),rates?[1,2]:[1],result.points.map(p=>[rates?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!),amountText(p.value,'price',p.priceTick),rates?amountText(p.discountFactor!,'factor'):instant(p.expiry!),rates?'Rate history':'Price history']));
  if(historyData)renderHistory();renderDependencies();renderInputs();
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
function renderHistory(){
  if(!result||!historyData)return;const p=result.points.find(p=>pointIdentity(p)===contract);if(!p)throw Error('Please select this sample again.');const rates=result.kind==='rates';$('history-card').hidden=false;text('history-title',`${rates?displayDate(pointDate(p)):deliveryLabel(p.deliveryFrom!,p.deliveryTo!)} · ${rates?'rate':'price'} history`);
  const labels=timeLabels(historyData.points.map(p=>p.at),prefs.timeZone);
  const exportHistory={snapshotId:result.snapshotId,curve:result.curve,...(rates?{seriesId:contract}:{contractId:contract})};
  renderChart($('history-chart'),{exportImage:download=>void act(async()=>{try{await api('history',exportHistory);}catch(error){clearPrices();throw error;}download();}),title:`${rates?'Rates':'Prices'} over time`,axis:'observation',unit:$('unit').textContent??'',series:[{name:rates?'Zero rate':'Price',points:historyData.points.map((p,i)=>({x:p.snapshotId,label:labels[i],y:p.value}))}]});
  table('history-values',['Market date',rates?'Zero rate (%)':'Price'],historyData.points.map((p,i)=>[labels[i],historyPriceLabel(p)]),[1],historyData.points.map((p,i)=>[labels[i],p.value===null?historyPriceLabel(p):amountText(p.value,'price',decimalTick())]));
}
async function history(){historyRequested=true;if(!result||!contract)return;const owner=result,selected=contract,started=marketGeneration,scope=marketSelection();clearChart($('history-chart'));historyData=undefined;$('history-card').hidden=true;const next=await api<PriceHistory>('history',{snapshotId:result.snapshotId,curve:result.curve,...(result.kind==='rates'?{seriesId:contract}:{contractId:contract})});if(result!==owner||contract!==selected||started!==marketGeneration||scope!==marketSelection())throw Error('The account or market selection changed. Reload before continuing.');historyData=next;renderHistory();}
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
    const convention=rateConvention();
    if(showAllContracts){for(const container of [sources,dependencies]){const host=el('section');renderWorkbook(host,rateWorkbook(result));container.append(host);}}
    for(const point of points){
      if(point.annualRate===undefined||point.actualDays===undefined||point.discountFactor===undefined||point.displayDiscountFactor===undefined)throw Error('The rate calculation details are unavailable.');
      if(!showAllContracts)for(const container of [sources,dependencies]){const section=el('section');section.className='contract-flow pricing-flow';section.append(el('h4',displayDate(pointDate(point))));const flow=el('div');flow.className='rate-flow';
        for(const [title,value,detail] of [['Zero rate',`${point.value}%`,'Annual rate'],['Time to maturity',`${point.actualDays} / 365`,convention.dayCount],['Discount factor',point.displayDiscountFactor,convention.compounding]]){const card=el('div');card.className='rate-step';card.append(el('small',title),el('strong',value),el('span',detail));flow.append(card);}
        section.append(flow);container.append(section);}
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

function referenceName(table:string,code:string,references=presentedReferences(data())){const r=exactRow(references[table]??[],'code',code);if(typeof r.name==='string')return r.name.replace(/^Synthetic /,'');if(typeof r.symbol==='string')return r.symbol;if(typeof r.display_code==='string')return r.display_code;return friendly(code);}
const linkedTables:Record<string,string>={commodity_code:'commodities',fuel_commodity_code:'commodities',output_commodity_code:'commodities',currency_code:'currencies',quote_currency_code:'currencies',unit_code:'units',lot_unit_code:'units',from_unit_code:'units',to_unit_code:'units',quote_unit_code:'units',fuel_unit_code:'units',output_unit_code:'units',location_code:'locations',type_code:'location_types',exchange_code:'exchanges',clearing_house_code:'clearing_houses',source_code:'sources',qualification_source_code:'sources',product_code:'futures_products',series_code:'futures_series',clock_code:'clock_rules',market_clock_code:'clock_rules',calendar_code:'calendars',trading_calendar_code:'calendars',quantity_code:'quantity_specifications',basis_code:'measurement_bases',index_a_code:'price_index_definitions',index_b_code:'price_index_definitions',numerator_quantity_code:'quantity_specifications',denominator_quantity_code:'quantity_specifications'};
function referenceValue(r:Row,key:string){
  if(r[key]===null)return '—';const value=val(r,key);if(value==='true'||value==='false')return value==='true'?'Yes':'No';if(linkedTables[key]){const linked=linkedTables[key],rows=displayReferenceRows(linked,referencePage!.references[linked]??[],referencePage!.referenceDisplay,referencePage!.referenceRevision);return referenceName(linked,value,{[linked]:rows});}
  if(key.endsWith('_at')||key==='eligible_from')return instant(value);if(key==='code')return friendly(value);
  if(/^-?\d+(\.\d+)?$/.test(value)||['name','symbol','display_code','source_series_code','zone_name','series_label','delivery_from','delivery_to','event_date','date','locator','evidence'].includes(key))return value;
  return friendly(value);
}
function clearReference(){referencePage=undefined;referenceEditor.setPage(undefined);unitConverter.reset();$('reference').replaceChildren();text('reference-page-status','');}
function referenceCategory(){
  const groups=dataSections(Object.keys(data().references)),sections=groups.flatMap(g=>g.sections),previous=select('reference-table').value;
  const selected=sections.some(s=>s.value===previous)?previous:'currencies';
  if(!sections.some(s=>s.value===selected))throw Error('These details are unavailable. Please refresh the page.');
  select('reference-table').replaceChildren(...groups.map(g=>{const group=document.createElement('optgroup');group.label=g.name;group.append(...g.sections.map(s=>{const option=el('option',s.name);option.value=s.value;return option;}));return group;}));select('reference-table').value=selected;
  const group=groups.find(g=>g.sections.some(s=>s.value===selected))!;
  const section=group.sections.find(s=>s.value===selected)!;
  text('data-title',section.name);text('data-description',section.consumer);
  return selected;
}
function renderReference(){
  if(contextDirty||!referencePage)return;
  const {table:selected,rows,offset,hasMore}=referencePage;
  if(data().unitCatalog)unitConverter.populate({...data(),references:data().unitCatalog!.references},selected==='units'||selected==='unit_conversions');else unitConverter.reset();
  const columns=publicColumns(rows),displayRows=displayReferenceRows(selected,rows,referencePage.referenceDisplay,referencePage.referenceRevision);table('reference',columns.map(c=>c.name),displayRows.map(r=>columns.map(c=>referenceValue(r,c.key))));
  referenceEditor.setPage({...referencePage,rows:displayRows});
  const filter=$('reference').querySelector<HTMLInputElement>('.table-search')!;filter.placeholder='Filter this page…';filter.setAttribute('aria-label','Filter this page');
  text('reference-page-status',rows.length?`Rows ${offset+1}–${offset+rows.length}${hasMore?canAdvanceReference()?' · more available':' · Refine your search to see more results.':''}`:'No rows on this page.');
}
async function loadReference(offset=0,search=''){
  clearReference();if(contextDirty)throw Error('Apply the changed context first.');await loadWorkspace();
  const owner=data(),selected=referenceCategory(),context=historicalContext()??null;
  const query=new URLSearchParams({table:selected,offset:String(offset),search});
  const next=await api<ReferencePage>('reference/catalog?'+query);
  const unitCatalogPage=['units','unit_conversions','dimensions','commodities','measurement_bases','quantity_specifications'].includes(selected);
  const revision=owner.definitions.revisions[0],expected=unitCatalogPage?owner.unitCatalog?.revision as ReferencePage['referenceRevision']|undefined:{catalogId:val(revision,'reference_catalog_id'),revisionId:val(revision,'reference_revision_id')};
  const sameRevision=expected&&next.referenceRevision?.catalogId===expected.catalogId&&next.referenceRevision?.revisionId===expected.revisionId&&(unitCatalogPage||next.definitionRevisionId===val(revision,'id'));
  const sameRequest=next.table===selected&&next.offset===offset&&next.search===search;
  const sameContext=next.profile===owner.profile&&JSON.stringify(next.context)===JSON.stringify(context);
  const boundedPage=next.referenceScope==='selected_table'&&next.pageSize===200&&Number.isSafeInteger(next.maximumOffset)&&next.maximumOffset>=offset&&typeof next.hasMore==='boolean'&&Array.isArray(next.rows)&&next.rows.length<=200&&next.references;
  if(workspace!==owner||contextDirty||page!=='reference'||select('reference-table').value!==selected||!sameRevision||!sameRequest||!sameContext||!boundedPage)
    throw Error('The reference page is missing or inconsistent. Refresh and try again.');
  referencePage=next;try{renderReference();}catch(error){clearReference();throw error;}
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
  workspace=undefined;navigation=undefined;clearReference();setCurveNames([]);$('curves').replaceChildren();
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
 if(page==='market'&&curve){const selectedHistory=contract;await build();if(historyRequested&&selectedHistory){contract=selectedHistory;await history();}}
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
async function reload(selected:string){await loadNavigation();renderFrames(selected);if(curve)await loadWorkspace();renderContext();}
function updateFilters(){
  prefs.browseBy=select('browse-by').value as Preferences['browseBy'];prefs.market=select('commodity').value||null;persist();
  if(curve&&!navigationGroups(directory(),prefs.browseBy,prefs.market,prefs.marketType).some(g=>g.curves.some(c=>c.code===curve))){curve='';invalidateMarketDates();clearPrices();}
  renderContext();syncControls();
}
async function openProfile(){
  try{
    const current=(await api<{permissions:string[]}>('access')).permissions;
    if(JSON.stringify([...current].sort())!==JSON.stringify([...permissions].sort()))await loadIdentity();
  }catch(error){if((error as {code?:string}).code==='access_denied'){text('profile-access','Workspace access could not be verified.');await loadIdentity();}throw error;}
  await profileChoices();
  choose('zone',zoneChoices(prefs.timeZone),prefs.timeZone);select('preferred-browse').value=prefs.browseBy;$<HTMLDialogElement>('profile-dialog').showModal();
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
$('preferences-form').onsubmit=event=>{event.preventDefault();void profileAction($('profile-save'),async()=>{const zone=select('zone').value;if(!validTimeZone(zone))throw Error('Choose a valid time zone.');const selected=select('snapshot').value,zoneChanged=zone!==prefs.timeZone;prefs.timeZone=zone;if(zoneChanged){workspaceCloses=undefined;closeBasis=undefined;closeCheckDue=true;}await prepareAmountDisplay({points:result?.points,historyData},request=>transport<string[]>('amounts/display',request),decimalTick());prefs.browseBy=select('preferred-browse').value as Preferences['browseBy'];persist();if(navigation){renderFrames(selected);renderContext();renderPrices();renderReference();schedules.refresh();dateEditor.refresh();power.refresh();}renderTimeZones();customCurves.refresh();jobs.refresh();administration.refresh();aiSettings.refresh();parties.refresh();intervalSeries.refresh();intervalCurves.refresh();ppa.refresh();futures.refresh();if(page==='futures'&&select('snapshot').value)await futures.load();$<HTMLDialogElement>('profile-dialog').close();});};
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
$('sign-out').onclick=()=>void act(async()=>{await api('logout',{});location.assign('/auth');});
async function loadIdentity(initialAccess?:unknown){
  customCurves.reset();$('custom-curves-page').hidden=true;jobs.reset();$('jobs-page').hidden=true;
  deliveryPrices.reset();intervalSeries.reset();intervalCurves.reset();ppa.reset();referenceEditor.reset();$('delivery-page').hidden=true;$('interval-series-page').hidden=true;$('interval-curves-page').hidden=true;parties.reset();unitConverter.reset();futures.reset();power.reset();touProducts.reset();schedules.reset();dateEditor.reset();administration.reset();aiSettings.reset();agentGrants.reset();contextDirty=false;$('parties-page').hidden=true;$('power-page').hidden=true;$('futures-page').hidden=true;$('no-access-page').hidden=true;
  curve='';clearPrices();clearReference();choose('reference-table',[],'');marketGeneration++;workspace=undefined;navigation=undefined;setCurveNames([]);permissions=[];choose('snapshot',[{value:'',name:'Choose a date…'}],'');marketCalendar.reset();workspaceCloses=undefined;closeBasis=undefined;select('snapshot').closest<HTMLElement>('.date-control')!.hidden=true;$('curves').replaceChildren();$('market-page').hidden=true;$('reference-page').hidden=true;$('schedules-page').hidden=true;$('curve-date-editor').hidden=true;$('admin-page').hidden=true;$('ai-page').hidden=true;$('date-context').hidden=true;
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
  if(read){await loadNavigation();renderFrames('');renderContext();if(!historicalContext())opening=await selectNewestClose();}else renderLayout();
  if(first!==null){
    await showPage(first);
    closeCheckDue=false; // the read that chose the close is this page's check for a newer one
    if(opening.kind==='choose')await openMarketCalendar(opening.date);
  }
  else{$('no-access-page').hidden=false;text('page-title','Workspace access');text('status','This account has no current workspace permissions.');}
}
$('open').onclick=()=>void profileAction($('open'),async()=>{text('environment-context-note','Current workspace · exact selected inputs');const next=select('profile').value;await api('profile',{profile:next});currentProfile=next;await loadIdentity();await profileChoices();$<HTMLDialogElement>('profile-dialog').close();});
// A newly selected As of close replaces what the previous one showed, and the badge takes its basis afresh.
function closeChanged(){
  closeBasis=undefined;
  marketCalendar.selectedChanged();customCurves.snapshotChanged();parties.reset();futures.reset();clearPrices();
}
async function loadSelectedClose(){
  if(!select('snapshot').value)return;
  if(page==='parties')await parties.load();else if(page==='futures')await futures.load();else if(page==='power'){power.reset();await power.load();}else if(page==='custom-curves')await customCurves.load();else if(page==='interval-curves')await intervalCurves.load();else if(page==='market'&&curve)await build();
}
$('snapshot').onchange=()=>void act(async()=>{closeChanged();await loadSelectedClose();});
$('example').onclick=()=>void act(async()=>{curve='';clearPrices();await currentInputs();const example=directory().example;if(!example)throw Error('No sample is configured.');const f=directory().frames.find(f=>directory().initialFrameIds.includes(f.id)&&new Date(f.at).getTime()===new Date(example.at).getTime());if(!f)throw Error('These sample prices are unavailable.');prefs.market=null;prefs.marketType='all';prefs.tab='prices';persist();curve=example.curve;clearPrices();renderFrames(f.id);await build();});
$('browse-by').onchange=()=>void act(async()=>updateFilters());$('commodity').onchange=()=>void act(async()=>updateFilters());
document.querySelectorAll<HTMLButtonElement>('[data-type]').forEach(b=>b.onclick=()=>void act(async()=>{prefs.marketType=b.dataset.type as Preferences['marketType'];updateFilters();}));
document.querySelectorAll<HTMLButtonElement>('.tab').forEach(b=>b.onclick=()=>{if(busy)return;prefs.tab=b.dataset.view as Preferences['tab'];persist();showView(prefs.tab);assistant.sync();});
const schedules=setupShortcutEditor(act,{time:api,load:()=>api('schedules'),preview:draft=>api('schedules/preview',draft),keep:draft=>api('schedules/keep',draft),resolve:body=>api('schedules/resolve',body)},()=>busy,()=>prefs.timeZone);
function dirtyContext(){stopLive();marketGeneration++;jobs.contextChanged();customCurves.contextChanged();deliveryPrices.reset();intervalSeries.reset();intervalCurves.reset();ppa.reset();parties.reset();clearReference();futures.reset();power.reset();contextDirty=true;clearPrices();text('environment-context-note','Context changed · choose View this context to apply it.');}
const parties=setupPartyEditors({api,act,busy:()=>busy,zone:()=>prefs.timeZone,profile:()=>administrationScope+':'+currentProfile,source:()=>{if(contextDirty)throw Error('Apply the changed context first.');return frame().id;}});
const administration=setupAdministration(act,api,()=>busy,()=>prefs.timeZone,()=>administrationScope+':'+currentProfile,profileChoices);
// A person's own agent grants, in the profile dialog (docs/ai-assistant.md §7.1).
const agentGrants=setupAgentGrants({api,act,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile});
$('profile-dialog').addEventListener('close',agentGrants.hide);
// Settings › AI, each tab offered by its own permission (docs/ai-assistant.md §9).
const aiSettings=setupAiSettings({api,act,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile,tabs:()=>aiTabs(permissions)});
const dateEditor=setupDateEditor(act,api,()=>busy,()=>prefs.timeZone,dirtyContext,()=>administrationScope+':'+currentProfile);
$('date-context').addEventListener('input',dirtyContext);
async function applyContext(){
  jobs.contextChanged();
  const context=await dateEditor.context();
  await viewContext(await api<{context:Context}>('market/context',{effectiveOn:context.effectiveOn,knownAt:context.knownAt,mode:'at',selection:'latest_correction'}));
}
async function viewContext(snapshot:{context:Context}){
  futures.reset();dateEditor.setContext(snapshot.context);contextDirty=false;clearPrices();await reload('');
  text('environment-context-note','Historical view · working snapshots use the latest known corrections; stored closes retain their original inputs. Changes are disabled.');
}
$('close-view-context').onclick=()=>{$<HTMLDetailsElement>('date-context').open=false;};
$('apply-view-context').onclick=()=>void act(async()=>{await applyContext();if(page==='reference')await loadReference();else if(page==='custom-curves')await customCurves.load();else if(page==='interval-series')await intervalSeries.load();else if(page==='interval-curves')await intervalCurves.load();});
async function currentInputs(){jobs.contextChanged();customCurves.contextChanged();intervalSeries.reset();intervalCurves.reset();ppa.reset();parties.reset();unitConverter.reset();futures.reset();power.reset();await api('market/current',{});contextDirty=false;dateEditor.clearContext();clearPrices();await reload('');text('environment-context-note','Current workspace · exact selected inputs');}
$('current-view-context').onclick=()=>void act(async()=>{await currentInputs();if(page==='reference')await loadReference();else if(page==='custom-curves')await customCurves.load();else if(page==='interval-series')await intervalSeries.load();else if(page==='interval-curves')await intervalCurves.load();});
$('return-working').onclick=()=>void act(async()=>{await currentInputs();if(page==='reference')await loadReference();else if(page==='custom-curves')await customCurves.load();else if(page==='interval-series')await intervalSeries.load();else if(page==='interval-curves')await intervalCurves.load();});
$('follow-live').onclick=()=>void act(async()=>{
 const selected=await api<LiveContextUpdate>('market/live',{snapshotId:frame().id,policy:'selected_facts_latest_committed_corrections_v1'});
 if(navigation){navigation.context=selected.context;navigation.live=selected.live;}
 contextDirty=false;connectLive(selected.live);lastLiveUpdate=selected;liveUpdates?.offer({...selected,generation:selected.live.generation});
});
$('live-retry').onclick=()=>{if(liveStream?.readyState===EventSource.CLOSED&&navigation?.live){liveStream=undefined;connectLive(navigation.live);}else if(lastLiveUpdate)liveUpdates?.offer({...lastLiveUpdate,generation:lastLiveUpdate.live.generation},true);};
const customCurves=setupCustomCurves($('custom-curves-page'),{api,act,marketScopeChanged:invalidateMarketDates,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile,historical:()=>contextDirty||!!historicalContext(),source:()=>{if(contextDirty)throw Error('Apply the changed market context first.');return {snapshotId:frame().id,context:historicalContext()};}});
// The hidden select remains the shared exact-frame adapter consumed by the
// existing pricing, trading and knowledge-context widgets. The calendar is its UI.
const marketCalendar=setupMarketCalendar($('market-date-calendar'),{
 zone:()=>prefs.timeZone,busy:()=>busy,enabled:()=>!!navigation&&can('ReadMarketData')&&!contextDirty,
 selected:()=>select('snapshot').value,current:()=>navigation?.frames.find(frame=>frame.id===select('snapshot').value),
 open:()=>void act(()=>openMarketCalendar()),newer:()=>void act(openNewestClose),select:id=>{select('snapshot').value=id;select('snapshot').dispatchEvent(new Event('change',{bubbles:true}));}
});
const deliveryPrices=setupPowerImports({api,act,busy:()=>busy,instant});
const ppa=setupPpa({api,act,busy:()=>busy,zone:()=>prefs.timeZone,actor:()=>administrationScope+':'+currentProfile,can,historical:()=>contextDirty||!!historicalContext()});
const intervalCurves=setupIntervalCurves({api,act,busy:()=>busy,zone:()=>prefs.timeZone,frame:()=>frame().id,unit:async quote=>{await loadWorkspace();return unit(quote);}});
const intervalSeries=setupIntervalSeries({api,act,busy:()=>busy,zone:()=>prefs.timeZone,identity:()=>administrationScope+':'+currentProfile});
const jobs=setupJobs({api,act,busy:()=>busy,identity:()=>administrationScope+':'+currentProfile,instant,zone:()=>prefs.timeZone});
const referenceEditor=setupReferenceEditor({host:$('reference-editor'),api,act,busy:()=>busy,identity:()=>administrationScope+':'+currentProfile,instant,onDenied:clearReference,onApplied:async()=>{
 const selected=select('snapshot').value,offset=referencePage?.offset??0,search=referencePage?.search??'';
 await reload(selected);await loadReference(offset,search);if(result)renderPrices();
}});
const power=setupPowerView({api,act,zone:()=>prefs.timeZone,name:curveName,source:()=>{if(contextDirty)throw Error('Apply the changed market context first.');const f=frame();return {snapshotId:f.id,curve,name:instant(f.at)};}});
const unitConverter=setupUnitConverter({api,act,busy:()=>busy});
const futures=setupFutures({newPpa:async()=>{await setPage('ppa');await ppa.load();$('page-title').tabIndex=-1;$('page-title').focus();},api,act,busy:()=>busy,canRunRisk:()=>permissions.includes('RunScenario'),bookingUnavailableReason:()=>contextDirty?'Apply the changed historical context first.':historicalContext()?'Historical view is read-only. Return to working data to book or amend.':!permissions.includes('BookTrade')?'Your current role does not have Book Trade permission.':'Choose an As of date to book a trade.',zone:()=>prefs.timeZone,profile:()=>administrationScope+':'+currentProfile,onReadContext:(at,known)=>text('header-calculation-time',`As of ${instant(at)} · knowledge cutoff ${instant(known)}`),source:()=>{if(contextDirty)throw Error('Apply the changed market context first.');return {snapshotId:frame().id,convention:directory().futuresConvention,context:historicalContext()};}});
const touProducts=setupTouProducts(act,api,()=>busy,()=>prefs.timeZone,async()=>{power.reset();if(select('snapshot').value)await power.load();});
$('reference-table').onchange=()=>void act(async()=>{$<HTMLInputElement>('reference-search').value='';await loadReference();});
$('reference-search-form').onsubmit=event=>{event.preventDefault();void act(()=>loadReference(0,$<HTMLInputElement>('reference-search').value));};
$('reference-previous').onclick=()=>void act(()=>loadReference(Math.max(0,referencePage!.offset-referencePage!.pageSize),referencePage!.search));
$('reference-next').onclick=()=>void act(()=>loadReference(referencePage!.offset+referencePage!.pageSize,referencePage!.search));
for(const id of ['source-contract','pricing-contract'])select(id).onchange=()=>void act(async()=>{pricingContract=select(id).value;input='';draftFor='';renderDependencies();renderInputs();});
for(const id of ['source-all','pricing-all'])$(id).onclick=()=>void act(async()=>{showAllContracts=!showAllContracts;renderDependencies();renderInputs();});
$('history-close').onclick=()=>{historyRequested=false;clearChart($('history-chart'));$('history-card').hidden=true;historyData=undefined;contract='';};
$('correction-form').onsubmit=event=>{event.preventDefault();void act(async()=>{const current=frame(),next=await api<{frame:Frame;observationId:string}>('corrections',{snapshotId:current.id,observationId:input,value:$<HTMLInputElement>('correction-value').value});closeCheckDue=true;clearPrices();await reload(next.frame.id);input=next.observationId;await build();notice('Your change was saved. The market date now shows the corrected prices. Earlier versions are still available.');});};
// Opens a page as its navigation does. The assistant's moves and Back open pages the same way.
async function showPage(next:string){
  switch(next){
    case 'market':await setPage('market');return;
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
    case 'futures':await setPage('futures');futures.reset();if(select('snapshot').value)await futures.load();return;
    default:throw Error('This page could not load. Please refresh.');
  }
}
// Pages that read the selected historical view apply a changed one before they open.
const readsContext=['market','custom-curves','delivery','reference','configuration','parties','futures'];
const navigate=(next:string)=>()=>void act(async()=>{if(contextDirty&&readsContext.includes(next))await applyContext();await showPage(next);});
for(const name of ['market','custom-curves','delivery','interval-series','interval-curves','jobs','power','reference','schedules','configuration','parties','admin','ai','futures','ppa'])
  $('nav-'+name).onclick=navigate(name);
$('curve-power').onclick=navigate('power');

// What the page shows, as the assistant's Looking at reports it and an ask sends it (docs/ai-assistant.md §8, §10.1).
function pageState():PageState{
  switch(page){
    case 'market':return {page,curve:curve||null,tab:prefs.tab};
    case 'power':return {page,frame:!!select('snapshot').value};
    case 'reference':return {page,table:referencePage?.table??'',search:referencePage?.search??''};
    case 'parties':return {page,kind:select('party-kind').value};
    case 'futures':return {page,...futures.report()};
    case 'configuration':case 'schedules':case 'delivery':case 'custom-curves':case 'interval-series':case 'interval-curves':case 'jobs':case 'admin':case 'ai':case 'ppa':return {page};
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
// kind, a Blotter view or a trade. A page target, and a view the page shows by itself, open as navigation opens them.
async function showTarget(report:string,selection:Record<string,unknown>|null){
  const next=webPage(report);
  if(!selection)return showPage(next);
  // What a target names is checked before the page changes, so a target the page cannot show leaves it as it was.
  switch(report){
    case 'custom_curves':{
      if(!Object.keys(selection).length)return showPage('custom-curves');
      const scope=()=>JSON.stringify([marketSelection(),historicalContext()?.contextId]);
      const requested=scope(),editor=customCurves.guard();
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
      if(selection.view==='curves'){curve='';invalidateMarketDates();clearPrices();renderContext();return;}
      prefs.tab=selection.view==='observations'?'inputs':prefs.tab==='inputs'?'prices':prefs.tab;persist();
      curve=code;invalidateMarketDates();clearPrices();
      if(select('snapshot').value)await build();else{await loadWorkspace();renderContext();}
      return;
    }
    case 'reference_data':{
      const table=String(selection.table),search=String(selection.search);await loadWorkspace();
      if(!dataSections(Object.keys(data().references)).some(g=>g.sections.some(t=>t.value===table)))throw Error('That reference table is not available here.');
      await setPage('reference');referenceCategory();select('reference-table').value=table;
      $<HTMLInputElement>('reference-search').value=search;await loadReference(0,search);return;
    }
    case 'parties':{
      const kind=String(selection.kind);
      if(![...select('party-kind').options].some(o=>o.value===kind))throw Error('That kind of party is not one this page shows.');
      await setPage('parties');select('party-kind').value=kind;await parties.load();return;
    }
    case 'blotter':
      await setPage('futures');futures.reset();
      if(selection.view==='trade'){
        if(select('snapshot').value)await futures.load();
        await futures.openTrade(String(selection.tradeId),selection.instrumentType==='financial_swap'?'financial_swap':'future',selection.revision as number|null);
      }else await futures.show(selection as Parameters<typeof futures.show>[0]);
      return;
    default:return showPage(next);
  }
}
// Selects knowledge and an As of frame, as a target pins them or a place showed them, before a page opens there.
async function selectContext(knowledge:TargetContext|{mode:'current'|'retained';contextId:string|null;frameId:string|null}){
  if(knowledge.mode==='current'){if(contextDirty||historicalContext())await currentInputs();}
  else if(contextDirty||historicalContext()?.contextId!==knowledge.contextId)await selectRetained(knowledge.contextId!);
  const frame=knowledge.frameId??'';
  if(select('snapshot').value===frame)return;
  if(frame&&!offeredFrames().some(f=>f.id===frame))throw Error('That As of date is not available in this session. Choose an As of date, or ask again.');
  renderFrames(frame);
  closeChanged();
}
// A historical view selected again by its id, exactly as it was captured.
async function selectRetained(contextId:string){
  jobs.contextChanged();customCurves.contextChanged();intervalSeries.reset();intervalCurves.reset();parties.reset();unitConverter.reset();futures.reset();power.reset();
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
const assistant=setupAssistant({api,root:apiRoot,identity:()=>administrationScope+':'+currentProfile,zone:()=>prefs.timeZone,
  available:()=>can('UseAgents'),pageBusy:()=>busy,act,place,names:()=>({curve:curveName,table:tableName}),
  trade:()=>futures.report().trade?.reference,unsaved,open:target=>showTarget(target.page,target.selection),
  go:async target=>{if(target.kind!=='proposal')await selectContext(target.context);await showTarget(target.page,target.selection);},restore:restorePlace});
for(const id of ['trade-editor','swap-editor'])$(id).addEventListener('close',()=>assistant.sync());
$('risk-close').addEventListener('click',()=>assistant.sync());
// Coming back to the window asks whether a newer close arrived while it was away.
window.addEventListener('focus',()=>void recheckNewestClose());
let initialWorkspaceReady=false;
void act(async()=>{
const [examples,boot]=await Promise.all([transport<{id:string;name:string;url:string}[]>('examples',undefined,'/api/'),api<ProfileChoices & {csrfToken:string;profile:string|null;runId:string;tenantId:string;workspaceId:string;access?:StartupAccess|null}>(startupBootstrapPath)]);
const chooser=select('example-dataset');chooser.replaceChildren(...examples.map(e=>new Option(e.name,e.id)));chooser.value=exampleId;chooser.onchange=()=>{const selected=examples.find(e=>e.id===chooser.value);if(selected)location.assign(selected.url);};
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
