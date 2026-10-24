// The pages as the assistant panel sees them (docs/ai-assistant.md §7.3, §8, §10.1, D191): the report an ask sends for
// what a page shows, Looking at, which says exactly that, the context an ask pins, when a navigation target moves the
// page, the places a move left for Back, and the plain lines the conversation shows. None of them names an identifier.
import {readsPhrase,type Vendor} from './agent-grants.js';
import {readPages,type Step,type Ending,type Usage,type TargetContext} from './assistant-events.js';

export type PageReport={page:string;selection:Record<string,unknown>};
export type BlotterSelection={mode:'trades'|'pivot';bookIds:string[];portfolioIds:string[];groupBy:string[];measures:string[]};
export type TradeShown={tradeId:string;instrumentType:'future'|'financial_swap';revision:number;reference:string};
// What a web page shows, as the page's own state names it.
export type PageState=
  {page:'market';curve:string|null;tab:'prices'|'dependencies'|'inputs'}|{page:'power';frame:boolean}|{page:'configuration'}|{page:'schedules'}
  |{page:'reference';table:string;search:string}|{page:'parties';kind:string}|{page:'futures';risk:boolean;trade:TradeShown|null;view:BlotterSelection}
  |{page:'delivery'|'custom-curves'|'interval-series'|'interval-curves'|'jobs'|'admin'|'ai'|'ppa'};
// The knowledge a page shows: working data, with its As of frame once one is chosen; the selected historical view; or a
// historical selection not yet applied, which pins nothing.
export type PageContext={mode:'current';frameId:string|null;frameLabel:string|null}
  |{mode:'retained';contextId:string;effectiveOn:string;knownAtLabel:string;frameId:string|null;frameLabel:string|null}|{mode:'unapplied'};

export const inconsistent=()=>Error('The assistant’s page is inconsistent. Reload it.');
const unsupported:Record<string,string>={delivery:'delivery_prices','interval-series':'interval_series',
  'interval-curves':'interval_curves',jobs:'jobs',admin:'users_access',ai:'settings_ai',ppa:'ppa'};

// The report an ask sends: a read page's selection is its read tool's input for the view the page shows; any other
// page's is empty. Blotter trade rows name no grouping, which the Blotter keeps for when its totals are shown again.
export function pageReport(state:PageState):PageReport{
  switch(state.page){
    case 'market':return {page:'curves',selection:state.curve===null?{view:'curves',curve:null}:{view:state.tab==='inputs'?'observations':'curve',curve:state.curve}};
    case 'power':return {page:'power_tous',selection:{view:state.frame?'power_hours':'tou_products'}};
    case 'configuration':return {page:'contract_definitions',selection:{view:'contract_definitions',curve:null}};
    case 'custom-curves':return {page:'custom_curves',selection:{}};
    case 'schedules':return {page:'schedules',selection:{view:'library'}};
    case 'reference':return {page:'reference_data',selection:{table:state.table,search:state.search}};
    case 'parties':return {page:'parties',selection:{kind:state.kind}};
    case 'futures':{
      if(state.risk)return {page:'blotter_risk',selection:{}};
      const t=state.trade,v=state.view,pivot=v.mode==='pivot';
      return {page:'blotter',selection:t?{view:'trade',mode:null,bookIds:null,portfolioIds:null,groupBy:null,measures:null,tradeId:t.tradeId,instrumentType:t.instrumentType,revision:t.revision}
        :{view:'blotter',mode:v.mode,bookIds:[...v.bookIds],portfolioIds:[...v.portfolioIds],groupBy:pivot?[...v.groupBy]:[],measures:pivot?[...v.measures]:[],
          tradeId:null,instrumentType:null,revision:null}};
    }
    default:return {page:unsupported[state.page],selection:{}};
  }
}

// The knowledge an ask pins: working data only at a chosen As of frame, the selected historical view with or without one.
export function askKnowledge(context:PageContext){
  if(context.mode==='unapplied'||context.mode==='current'&&!context.frameId)return undefined;
  return context.mode==='current'?{mode:'current',contextId:null,frameId:context.frameId}:{mode:'retained',contextId:context.contextId,frameId:context.frameId};
}

const pageNames:Record<string,string>={curves:'Curves & prices',contract_definitions:'Contract definitions',schedules:'Schedules',power_tous:'Power TOUs',
  reference_data:'Reference data',parties:'Entities & counterparties',blotter:'Blotter',blotter_risk:'Blotter',delivery_prices:'Delivery prices',
  custom_curves:'Custom curves',interval_series:'Interval data',interval_curves:'Curves & prices · Hourly / sub-hourly',jobs:'Jobs & Infrastructure',users_access:'Users & access',
  settings_ai:'AI',ppa:'PPA bookings'};
const dimensions:Record<string,string>={portfolio:'portfolio',book:'book',market:'market',contract:'contract',instrumentType:'instrument type',commodity:'commodity'};
const counted=(n:number,noun:string)=>`${n} ${noun}${n===1?'':'s'}`;
const joined=(names:string[])=>names.length>1?`${names.slice(0,-1).join(', ')} and ${names.at(-1)}`:names[0];
export type Names={curve:(code:string)=>string;table:(code:string)=>string};

function shows(report:PageReport,names:Names,trade?:string){
  const s=report.selection;
  switch(report.page){
    case 'curves':return s.view==='curves'?'Curve directory':`${s.view==='observations'?'Inputs':'Prices'} of ${names.curve(String(s.curve))}`;
    case 'contract_definitions':return 'Contract date definitions';
    case 'custom_curves':return 'Published curve ingredients and saved curve library. Unsaved edits are not sent.';
    case 'schedules':return 'Schedule library';
    case 'power_tous':return s.view==='power_hours'?'TOU products and power hours':'TOU products';
    case 'reference_data':return s.table===''?'No table is shown yet':names.table(String(s.table))+(s.search?` · rows matching “${s.search}”`:'');
    case 'parties':return s.kind==='legal_entity'?'Legal entities':'Counterparties';
    case 'blotter':{
      if(s.view==='trade'){if(!trade)throw inconsistent();return `Trade ${trade}, revision ${s.revision}`;}
      const books=(s.bookIds as string[]).length,portfolios=(s.portfolioIds as string[]).length;
      const scope=books||portfolios?[books?counted(books,'book'):'',portfolios?counted(portfolios,'portfolio'):''].filter(Boolean).join(', '):'every book you can read';
      return `${s.mode==='pivot'?`Totals by ${joined((s.groupBy as string[]).map(d=>dimensions[d]))}`:'Trade rows'} · ${scope}`;
    }
    case 'blotter_risk':return 'The assistant does not read risk results.';
    default:return 'The assistant does not read this page.';
  }
}
function contextLine(context:PageContext){
  if(context.mode==='unapplied')return 'Historical selection not applied · apply it to ask about it';
  const frame=context.frameLabel?`As of ${context.frameLabel}`:'';
  if(context.mode==='current')return `Working data · ${frame||'choose an As of date to ask about it'}`;
  return `Historical view · ${context.effectiveOn}, as known at ${context.knownAtLabel} · ${frame||'no As of date chosen'}`;
}
// Looking at: the page, what it shows and the context the next question will pin, or that the page is not read.
export function lookingAt(report:PageReport,context:PageContext,names:Names,extra:{trade?:string}={}){
  if(!Object.hasOwn(pageNames,report.page))throw inconsistent();
  // A page whose report names no selection yet, Reference data before it shows a table, has nothing to be asked from.
  const named=!(report.page==='reference_data'&&report.selection.table==='');
  return {page:pageNames[report.page],shows:shows(report,names,extra.trade),context:contextLine(context),supported:readPages.includes(report.page),
    askable:named&&askKnowledge(context)!==undefined};
}

// A target changes context when its pinned knowledge or As of frame is not what the page has selected.
export function changesContext(target:TargetContext,page:PageContext){
  if(page.mode==='unapplied')return true;
  return target.mode==='current'?page.mode!=='current'||page.frameId!==target.frameId
    :page.mode!=='retained'||page.contextId!==target.contextId||page.frameId!==target.frameId;
}
// Only a target that arrives live, keeps the context and leaves nothing unsaved moves the page; any other waits for Go.
export const targetAction=(target:{live:boolean;changesContext:boolean;unsaved:boolean;proposal?:boolean})=>target.live&&!target.changesContext&&!target.unsaved&&!target.proposal?'move':'go';

const webPages:Record<string,string>={curves:'market',contract_definitions:'configuration',schedules:'schedules',power_tous:'power',reference_data:'reference',
  parties:'parties',blotter:'futures',custom_curves:'custom-curves'};
// The web page a target opens: only one the assistant reads.
export function webPage(page:string){if(!Object.hasOwn(webPages,page))throw inconsistent();return webPages[page];}

// A place a move left: the web page, what it showed in report form and the context it showed it in.
export type Place={page:string;report:PageReport;context:PageContext};
// The places this tab's moves left, for their notices' Back, kept in memory only: a reload forgets them.
export class NavigationHistory{
  #places=new Map<string,Place>();
  #moves=0;
  record(place:Place){const key=`move-${++this.#moves}`;this.#places.set(key,structuredClone(place));return key;}
  origin(key:string){const place=this.#places.get(key);return place&&structuredClone(place);}
  clear(){this.#places.clear();}
}

const toolNames:Record<string,string>={read_current_page:'Read the page',read_market_data:'Read market data',read_trades:'Read trades',
  read_trade_parties:'Read trade parties',read_schedules:'Read schedules',read_reference_data:'Read reference data',read_custom_curves:'Read curve ingredients',prepare_custom_curve:'Prepare proposed curve',navigate:'Navigate'};
const reasons:Record<string,string>={tool_arguments_invalid:'the request was not one this tool accepts',result_too_large:'the result was larger than one read returns',
  page_not_supported:'the assistant does not read the page you asked from',tool_not_enabled:'this agent does not read the page you asked from',
  page_selection_unavailable:'what the page showed is not available in the pinned context',snapshot_missing:'it needs an As of date, and none was chosen',
  power_historical:'a historical view does not read current definitions',access_denied:'the agent’s access does not cover it',
  missing_curve:'that curve is not in the pinned market data',missing_observation:'market data it needs is missing',blotter_view:'that Blotter view is not available',
  futures_trade:'that trade is not available in the pinned context',swap_trade:'that trade is not available in the pinned context',tool_failed:'the read failed'};
// A step: the tool's name and, for a refusal or failure, why in plain words; a withheld earlier turn has a line of its own.
export function stepLine(step:Step){
  if('withheld' in step)return 'An earlier answer was left out: what it read can no longer be read for you';
  if(!Object.hasOwn(toolNames,step.tool))throw inconsistent();
  if(step.code===null)return toolNames[step.tool];
  return `${toolNames[step.tool]} · ${Object.hasOwn(reasons,step.code)?reasons[step.code]:'the tool refused this request'}`;
}

const tooLong='This conversation is too long to continue. Start a new conversation.';
const failures:Record<string,string>={limit_elapsed:'The answer ran out of time.',limit_tool_steps:'The answer used as many reads as this agent may make.',
  limit_output_tokens:'The answer reached this agent’s length limit.',limit_input_tokens:tooLong,context_window_exceeded:tooLong,limit_input_unverifiable:tooLong,
  access_changed:'The answer stopped because access to what it read changed.',tool_failed:'A read failed, so the answer could not finish.'};
const cancellations:Record<string,string>={session_ended:'The answer stopped because your session changed.',
  access_changed:'The answer stopped because your grant to this agent ended.',agent_retired:'The answer stopped because the agent was retired.',
  model_disabled:'The answer stopped because its model was switched off.',model_provider_disabled:'The answer stopped because its provider was switched off.'};
// Why an answer stopped; a completed answer needs no line.
export function endingLine(ending:Ending){
  switch(ending.state){
    case 'completed':return null;
    case 'interrupted':return 'The answer was interrupted when the assistant restarted.';
    case 'cancelled':return ending.code===null?'You stopped this answer.':Object.hasOwn(cancellations,ending.code)?cancellations[ending.code]:'The answer was stopped.';
    default:return ending.code!==null&&Object.hasOwn(failures,ending.code)?failures[ending.code]:'The answer could not finish.';
  }
}
export const startsOver=(ending:Ending)=>endingLine(ending)===tooLong;

const grouped=new Intl.NumberFormat('en-US',{maximumFractionDigits:0});
const figure=(d:{count:number;unknownCalls:number})=>d.unknownCalls?'unknown':grouped.format(d.count);
// Tokens by dimension and each currency's exact cost as the payload carries it, its code included; unknown and
// unpriced say so rather than showing a partial figure.
export function usageLine(usage:Usage){
  if(usage.calls===0)return 'No model calls';
  if(!usage.cost.length)throw inconsistent();
  const cost=usage.cost.map(c=>`${c.currency.code} ${c.amount===null?'unpriced':c.amount}`).join(', ');
  return `Tokens: ${figure(usage.uncachedInput)} input · ${figure(usage.cacheRead)} cache read · ${figure(usage.cacheWrite)} cache write · `
    +`${figure(usage.output)} output (${figure(usage.reasoning)} reasoning) · Cost: ${cost}`;
}

export type UsableAgent={id:string;name:string;version:number;draft:boolean;model:{id:string;version:number;vendor:string;identifier:string;displayName:string};
  permissions:string[];grantDays:number;grant:{id:string;expiresAt:string}|null};
const vendorName=(vendors:Vendor[],code:string)=>{const vendor=vendors.find(v=>v.code===code);if(!vendor)throw inconsistent();return vendor.name;};
// A usable version as the picker lists it.
export const agentChoice=(agent:UsableAgent,vendors:Vendor[])=>
  `${agent.name} · version ${agent.version} · ${vendorName(vendors,agent.model.vendor)} ${agent.model.displayName}${agent.draft?' · Draft':''}`;
// The last explicit choice, while it is listed; a stored value in any other form chooses nothing.
export function preselect(agents:UsableAgent[],stored:string|null){
  try{
    const v=JSON.parse(stored??'null');
    if(!v||typeof v!=='object'||Object.keys(v).length!==2||typeof v.agentId!=='string'||!Number.isSafeInteger(v.version))return undefined;
    return agents.find(a=>a.id===v.agentId&&a.version===v.version);
  }catch{return undefined;/* Invalid browser state chooses nothing. */}
}

// The version a reopened conversation asks with: the one its last question used while that is listed, otherwise the only
// listed version of its agent; with neither, the person chooses.
export function conversationChoice(agents:UsableAgent[],agentId:string,version:number){
  const listed=agents.filter(a=>a.id===agentId);
  return listed.find(a=>a.version===version)??(listed.length===1?listed[0]:undefined);
}

const unavailableModel='This agent’s model is not available right now. Ask an administrator, or choose another agent.';
const messages:Record<string,string>={turn_active:'Wait for your current answer to finish, or stop it, before asking again.',
  workspace_turn_limit:'This workspace is answering as many questions as it can at once. Ask again shortly.',
  question_too_long:'This question is longer than the assistant accepts. Shorten it and send it again.',
  conversation_not_found:'This conversation is no longer available. Start a new conversation.',
  conversation_agent_mismatch:'This conversation is with another agent. Start a new conversation to ask this one.',
  knowledge_budget_reserved:'The workspace has no saved contexts left for working data. Select a historical view to ask over it.',
  access_changed:'This agent no longer has the access an earlier answer in this conversation used, so it cannot continue it. Start a new '
    +'conversation, or ask an administrator to restore the agent\'s access.',
  context_required:'Ask over the knowledge the page shows: working data, or the selected historical view.',
  snapshot_missing:'Choose an As of date to ask about working data.',page_report_invalid:'The page could not say what it shows. Reload it and ask again.',
  agent_runtime_unavailable:'The assistant is not running right now. Try again shortly.',
  agent_not_allowed:'This agent version is not one you may use. Choose another agent.',agent_retired:'This agent is retired. Choose another agent.',
  grant_permission_missing:'You lack a permission this agent version’s tools need.',access_denied:'Your current access cannot use the assistant.',
  turn_not_found:'You have no such question here.',session_ended:'Your session changed. Reload the page to continue.',
  grant_required:'Allow this agent version its reads before asking it.',
  model_changed:unavailableModel,model_disabled:unavailableModel,model_unqualified:unavailableModel,model_unpriced:unavailableModel,
  model_provider_disabled:unavailableModel,model_provider_unqualified:unavailableModel,credential_unavailable:unavailableModel};
// Why the panel's own request was refused; a code it does not name keeps the page's message.
export const panelMessage=(code:string|undefined)=>code!==undefined&&Object.hasOwn(messages,code)?messages[code]:undefined;

const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
export const isUuid=(value:unknown):value is string=>typeof value==='string'&&uuid.test(value);
const exact=(value:unknown,names:string[])=>!!value&&typeof value==='object'&&!Array.isArray(value)&&Object.keys(value).length===names.length
  &&names.every(n=>Object.hasOwn(value,n));
// An ask kept in this tab's storage, read back only in the exact form the panel stores it, as a request whose outcome is
// unknown: Retry sends it as it was first sent, and the server answers a repeat as it answered the original.
export function keptAsk(stored:string|null){
  try{
    const v=JSON.parse(stored??'null'),b=v?.body,k=b?.knowledge;
    if(!exact(v,['operationId','path','body'])||!isUuid(v.operationId)||v.path!=='assistant/turns/ask'
      ||!exact(b,['conversationId','agentId','version','question','pageReport','knowledge'])||b.conversationId!==null&&!isUuid(b.conversationId)
      ||!isUuid(b.agentId)||!Number.isSafeInteger(b.version)||b.version<1||typeof b.question!=='string'||!b.question.trim()
      ||!exact(b.pageReport,['page','selection'])||!exact(k,['mode','contextId','frameId']))return undefined;
    return {operationId:v.operationId as string,path:v.path as string,body:b as Record<string,unknown>,uncertain:true};
  }catch{return undefined;/* Invalid browser state keeps no request. */}
}

// The date a grant given now would end in the person's zone: it runs its days from the instant it is given.
export function grantExpiry(now:Date,days:number,zone:string){
  const parts=new Intl.DateTimeFormat('en-US',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date(now.getTime()+days*86_400_000));
  const part=(type:string)=>parts.find(p=>p.type===type)!.value;
  return `${part('year')}-${part('month')}-${part('day')}`;
}
// The consent §7.1 asks for, naming only the reads the version's switched-on tools need.
export function consentText(agent:Pick<UsableAgent,'name'|'model'|'permissions'>,vendors:Vendor[],until:string){
  return `Allow ${agent.name}, which sends your questions and the data it reads to ${vendorName(vendors,agent.model.vendor)} (${agent.model.displayName}) through `
    +`the vendor account your administrators configured, whose owner may be able to see them, to read ${readsPhrase(agent.permissions)} on your behalf until ${until}?`;
}
