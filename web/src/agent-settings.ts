import {renderRegularTable} from './regular-table.js';
import {displayInstant,publicText} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Limits={toolSteps:number;inputTokens:number;outputTokens:number;elapsedSeconds:number;retries:number};
type VersionModel={id:string;version:number;vendor:string;identifier:string;displayName:string;current:boolean};
type AgentVersion={version:number;description:string;instructions:string;model:VersionModel;reasoning:string;caching:boolean;tools:string[];allowedRoles:string[];
  limits:Limits;grantDays:number;savedBy:string;savedAt:string};
type Agent={id:string;name:string;version:number;state:string;publishedVersion:number|null;newest:AgentVersion|null;published:AgentVersion|null};
type Model={id:string;version:number;vendor:string;identifier:string;displayName:string;outputLimit:number;reasoningEfforts:string[]};
type Choices={vendors:{code:string;name:string}[];roles:{code:string;name:string;permissions:string[]}[];tools:{code:string;permissions:string[]}[];models:Model[];
  bounds:{instructions:number;grantDays:number}};
type Directory=Choices&{agents:Agent[];audit:{operationId:string;at:string;actor:string;action:string;agent:string}[]};
export type AiTab='models'|'providers'|'agents';
export type AgentEntry={description:string;instructions:string;modelId:string;reasoning:string;caching:string;tools:string[];allowedRoles:string[];
  limits:Record<keyof Limits,string>;grantDays:string};
// A model as it was chosen: the exact capability version the choice named.
export type ModelPin={id:string;version:number};

const inconsistent=()=>Error('The AI settings directory is inconsistent. Refresh AI settings.');
const grouped=(count:number)=>count.toLocaleString('en-US');
const changes:Record<string,string>={create_agent:'Agent created',save_agent:'Version saved',publish_agent:'Version published',retire_agent:'Agent retired'};
const states:Record<string,string>={active:'Active',retired:'Retired'};
const toolNames:Record<string,string>={navigate:'Navigate',read_current_page:'Read current page',read_market_data:'Read market data',read_reference_data:'Read reference data',
  read_schedules:'Read schedules',read_trade_parties:'Read trade parties',read_trades:'Read trades'};
const limitNames:[keyof Limits,string][]=[['toolSteps','tool steps per turn'],['inputTokens','input tokens per turn'],['outputTokens','output tokens per call'],
  ['elapsedSeconds','time per turn in seconds'],['retries','retries per call']];
// Refusals that prove a first attempt wrote nothing, so its retained request can be released.
const rejections=['model_admin_input','model_admin_conflict','agent_retired','agent_role_lacks_permission','model_changed','model_disabled','model_unqualified',
  'model_unpriced','model_provider_disabled','model_provider_unqualified','parameter_required','model_limit_exceeded','grant_duration_exceeded','instructions_too_long',
  'admin_limit','definition_rejected','access_denied','request_format','request_token'];
const paths=['create','save','publish','retire'].map(path=>'settings/ai/agents/'+path);
const roleAdvice='Allow only roles that use agents and hold every permission its tools need.';
function toolName(code:string){if(!Object.hasOwn(toolNames,code))throw inconsistent();return toolNames[code];}

// Models & prices and Providers need ManageModelProviders; Agents needs ManageAgents (§9).
export function aiTabs(permissions:string[]):AiTab[]{
  return [...permissions.includes('ManageModelProviders')?['models','providers'] as const:[],...permissions.includes('ManageAgents')?['agents'] as const:[]];
}
export function agentChange(action:string){if(!Object.hasOwn(changes,action))throw inconsistent();return changes[action];}
export function agentState(agent:{state:string}){if(!Object.hasOwn(states,agent.state))throw inconsistent();return states[agent.state];}
// Marked while the published version's model version is no longer the model's current one.
export function modelChanged(agent:Agent){
  if(!agent.published)return false;if(typeof agent.published.model.current!=='boolean')throw inconsistent();return !agent.published.model.current;
}
function vendorName(choices:Pick<Choices,'vendors'>,code:string){const vendor=choices.vendors.find(v=>v.code===code);if(!vendor)throw inconsistent();return vendor.name;}
const modelLabel=(choices:Pick<Choices,'vendors'>,m:{displayName:string;identifier:string;vendor:string})=>`${m.displayName} · ${m.identifier} (${vendorName(choices,m.vendor)})`;
export function agentRow(agent:Agent,choices:Choices,instant:(at:string)=>string){
  const newest=agent.newest?.version??0,published=agent.publishedVersion??0;
  if(published>newest||(agent.publishedVersion===null)!==(agent.published===null))throw inconsistent();
  const state=agentState(agent)+(agent.state==='active'&&modelChanged(agent)?' · model changed':'');
  return [agent.name,state,agent.newest?`Version ${newest}${newest>published?' · draft':''}`:'None saved',agent.published?`Version ${published}`:'Not published',
    agent.published?modelLabel(choices,agent.published.model):'',agent.newest?`${agent.newest.savedBy} · ${instant(agent.newest.savedAt)}`:''];
}
// A role a version allowed that no longer uses agents is still named, as it was saved.
const roleName=(choices:Choices,code:string)=>choices.roles.find(r=>r.code===code)?.name??`${code} (no longer uses agents)`;
export function versionRows(version:AgentVersion,choices:Choices,instant:(at:string)=>string){
  const {limits}=version;
  return [['Description',version.description],['Instructions',version.instructions],
    ['Model',`${modelLabel(choices,version.model)} · capability version ${version.model.version}${version.model.current?'':' · no longer current'}`],
    ['Reasoning effort',version.reasoning],['Prompt caching',version.caching?'On':'Off'],['Tools',version.tools.map(toolName).join(', ')||'None'],
    ['Allowed roles',version.allowedRoles.map(code=>roleName(choices,code)).join(', ')||'None'],['Tool steps per turn',String(limits.toolSteps)],
    ['Input tokens per turn',grouped(limits.inputTokens)],['Output tokens per call',grouped(limits.outputTokens)],['Time per turn',`${limits.elapsedSeconds} seconds`],
    ['Retries per call',String(limits.retries)],['Grant duration',`${version.grantDays} days`],['Saved',`${version.savedBy} · ${instant(version.savedAt)}`]];
}
// A version names a model the directory offers, at the capability version the directory lists for it.
export function modelChoices(choices:Choices){
  return choices.models.map(m=>({value:m.id,label:`${modelLabel(choices,m)} · capability version ${m.version}`}));
}
// A choice holds only while the directory lists its model at the capability version it named; nothing moves it to another.
export function keptModel(choices:Choices,pin:ModelPin|undefined){
  return pin&&choices.models.some(m=>m.id===pin.id&&m.version===pin.version)?pin:undefined;
}
export function outputHelp(choices:Choices,modelId:string){
  const model=choices.models.find(m=>m.id===modelId);
  return model?`Up to ${grouped(model.outputLimit)} tokens per call for this capability version.`:'';
}
function whole(value:string,message:string){const n=/^[0-9]+$/.test(value)?Number(value):NaN;if(!Number.isSafeInteger(n))throw Error(message);return n;}
// Every field is entered and none inferred; text travels exactly as entered and the server applies every rule.
export function agentSaveRequest(entry:AgentEntry,choices:Choices){
  if(!entry.description)throw Error('Enter a description.');
  if(!entry.instructions)throw Error('Enter the instructions.');
  if(!entry.modelId)throw Error('Choose a model.');
  const model=choices.models.find(m=>m.id===entry.modelId);if(!model)throw Error('Choose a model again after refreshing AI settings.');
  if(!entry.reasoning)throw Error('Choose a reasoning effort.');
  if(entry.caching!=='on'&&entry.caching!=='off')throw Error('Choose whether prompt caching is on.');
  const limits=Object.fromEntries(limitNames.map(([key,name])=>[key,whole(entry.limits[key],`Enter the ${name} as a whole number.`)])) as Limits;
  return {description:entry.description,instructions:entry.instructions,modelId:model.id,modelVersion:model.version,reasoning:entry.reasoning,caching:entry.caching==='on',
    tools:[...entry.tools],allowedRoles:[...entry.allowedRoles],limits,grantDays:whole(entry.grantDays,'Enter the grant duration as a whole number of days.')};
}
export function boundsHelp(choices:Choices){
  const {instructions,grantDays}=choices.bounds??{};
  if(!Number.isSafeInteger(instructions)||!Number.isSafeInteger(grantDays))throw inconsistent();
  return {instructions:`Up to ${grouped(instructions)} characters, in lines.`,grantDays:`Up to ${grantDays} days.`};
}
// The refusal names the role and the permission as fields; without them the page names neither.
export function agentRefusal(error:{code?:unknown;role?:unknown;permission?:unknown}){
  if(error.code!=='agent_role_lacks_permission')return undefined;
  return typeof error.role==='string'&&typeof error.permission==='string'
    ?`The allowed role ${error.role} lacks ${error.permission}, which this version needs. ${roleAdvice}`:`An allowed role lacks a permission this version needs. ${roleAdvice}`;
}
// The codes the agent commands share with the model commands, in the agents' terms.
const agentMessages:Record<string,string>={
  model_admin_input:'Check the entries: a name of 1 to 80 characters and a description of 1 to 500, each with no space at either end and no control, '
    +'line-separator or text-direction formatting characters; instructions in lines with those characters excluded and no space or line feed at either end; '
    +'1 to 100 tool steps and 1 to 100,000,000 input tokens per turn; 1 to 10,000,000 output tokens per call; 1 to 3,600 seconds per turn; '
    +'0 to 10 retries per call; and a grant of at least one day.',
  model_admin_conflict:'This agent changed, its name is taken, or the request identity was used for a different change. Refresh AI settings before trying again.',
  model_unqualified:'The model is not qualified: its capabilities must be recorded under the usage mapping its vendor’s adapter applies.'};
export function agentMessage(code:string|undefined){return code&&Object.hasOwn(agentMessages,code)?agentMessages[code]:undefined;}

// The Agents tab of Settings › AI (docs/ai-assistant.md §9).
export function setupAgentSettings(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const input=(id:string)=>get<HTMLInputElement>(id);
  const value=(id:string)=>get<HTMLInputElement|HTMLTextAreaElement|HTMLSelectElement>(id).value;
  const form=(id:string)=>get<HTMLFormElement>(id);
  const instant=(at:string)=>displayInstant(at,options.zone());
  const pending=new PendingCommand(code=>!!code&&rejections.includes(code));
  const storage=()=>`openetrm.ai.agents.request.${options.identity()}`;
  const limitFields:[keyof Limits,string][]=[['toolSteps','ai-agent-tool-steps'],['inputTokens','ai-agent-input-tokens'],['outputTokens','ai-agent-output-tokens'],
    ['elapsedSeconds','ai-agent-elapsed'],['retries','ai-agent-retries']];
  const tables=['ai-agent-table','ai-agent-audit','ai-agent-published','ai-agent-draft'];
  // The form's basis is the agent and newest version it was filled from; its model is pinned to the version chosen, and
  // the roles it chose that no longer use agents are kept apart. A confirmed save refills the form at the next read.
  let directory:Directory|undefined,selected:string|undefined,basis:{agent:string;newest:number}|undefined,pin:ModelPin|undefined,dropped:string[]=[],
    refill=false,generation=0;

  function store(){
    try{if(pending.command)sessionStorage.setItem(storage(),JSON.stringify(pending.command));else sessionStorage.removeItem(storage());}catch{/* The in-memory request is retained for this visit. */}
  }
  function stored():RequestCommand|undefined{
    try{
      const v=JSON.parse(sessionStorage.getItem(storage())??'null');
      return v&&typeof v.operationId==='string'&&paths.includes(v.path)&&v.body&&typeof v.body==='object'?{operationId:v.operationId,path:v.path,body:v.body,uncertain:true}:undefined;
    }catch{return undefined;/* Invalid browser state cannot grant authority. */}
  }
  const agent=()=>directory?.agents.find(a=>a.id===selected);
  function current(){const found=agent();if(!found)throw Error('Choose an agent again after refreshing AI settings.');return found;}
  const stale=(a:Agent|undefined)=>!!a&&basis?.agent===a.id&&basis.newest!==(a.newest?.version??0);
  function button(label:string,work:()=>void){const b=document.createElement('button');b.type='button';b.className='text-button';b.textContent=publicText(label);b.onclick=()=>{if(!options.busy())work();};return b;}
  function boxes(host:string,items:{value:string;label:string;detail:string}[],chosen:Set<string>){
    get(host).replaceChildren(...items.map(item=>{
      const label=document.createElement('label'),box=document.createElement('input'),detail=document.createElement('span');
      box.type='checkbox';box.value=item.value;box.checked=chosen.has(item.value);detail.className='field-help';detail.textContent=publicText(item.detail);
      label.append(box,' '+publicText(item.label)+' ',detail);return label;
    }));
  }
  const checked=(host:string)=>[...get(host).querySelectorAll<HTMLInputElement>('input:checked')].map(box=>box.value);
  function fillSelect(id:string,first:string,choices:{value:string;label:string}[],chosen:string){
    const node=get<HTMLSelectElement>(id);node.replaceChildren(new Option(first,''),...choices.map(c=>new Option(publicText(c.label),c.value)));
    node.value=choices.some(c=>c.value===chosen)?chosen:'';
  }
  // The choices follow the directory. The model stays chosen only at the capability version it was chosen at, and
  // its effort only with it, or the form says why it is unchosen; tools stay checked while offered. A chosen role that
  // no longer uses agents stays chosen, named rather than offered, and is checked again once it is offered again.
  function fillChoices(chosen:{effort:string;tools:string[];roles:string[]}){
    if(!directory)return;const offered=directory;
    if(pin&&!keptModel(directory,pin)){pin=undefined;get('ai-agent-model-pin-note').hidden=false;}
    fillSelect('ai-agent-model','Choose a model…',modelChoices(directory),pin?.id??'');
    const model=directory.models.find(m=>m.id===pin?.id);
    fillSelect('ai-agent-effort','Choose an effort…',(model?.reasoningEfforts??[]).map(e=>({value:e,label:e})),model?chosen.effort:'');
    get('ai-agent-output-help').textContent=outputHelp(directory,pin?.id??'');
    boxes('ai-agent-tools',directory.tools.map(t=>({value:t.code,label:toolName(t.code),detail:t.permissions.length?`Needs ${t.permissions.join(', ')}`:'Needs no permission of its own'})),
      new Set(chosen.tools));
    const roles=new Set([...chosen.roles,...dropped]);dropped=[...roles].filter(code=>!offered.roles.some(r=>r.code===code));
    boxes('ai-agent-roles',offered.roles.map(r=>({value:r.code,label:r.name,detail:r.permissions.join(', ')})),roles);
    const note=get('ai-agent-roles-note');note.hidden=!dropped.length;
    note.textContent=dropped.length?publicText(`A save leaves out ${dropped.join(', ')}: only roles that use agents can be allowed.`):'';
  }
  const entries=()=>({effort:value('ai-agent-effort'),tools:checked('ai-agent-tools'),roles:checked('ai-agent-roles')});
  // The form starts from the newest version, every field shown.
  function prefill(){
    if(!directory)return;const a=agent(),version=a?.newest;
    form('ai-agent-form').reset();get('ai-agent-model-pin-note').hidden=true;
    basis=a&&{agent:a.id,newest:version?.version??0};
    pin=version?{id:version.model.id,version:version.model.version}:undefined;
    dropped=[];
    fillChoices({effort:version?.reasoning??'',tools:version?.tools??[],roles:version?.allowedRoles??[]});
    get<HTMLSelectElement>('ai-agent-caching').value=version?(version.caching?'on':'off'):'';
    if(!version)return;
    get<HTMLTextAreaElement>('ai-agent-description').value=version.description;get<HTMLTextAreaElement>('ai-agent-instructions').value=version.instructions;
    for(const [key,id] of limitFields)input(id).value=String(version.limits[key]);
    input('ai-agent-grant-days').value=String(version.grantDays);
  }
  // Opening an agent starts its form afresh, and a Retire confirmation never carries over to another agent.
  function select(id:string){
    if(!directory?.agents.some(a=>a.id===id))return;
    selected=id;input('ai-agent-retire-confirm').checked=false;prefill();render();sync();get('ai-agent-card').scrollIntoView({block:'nearest'});
  }
  // The form's choices outlast a directory that could not be read; only an end of access removes them.
  function clear(){directory=undefined;for(const id of tables)get(id).replaceChildren();get('ai-agent-card').hidden=true;}
  // Nothing an identity entered or was shown outlives its access.
  function wipe(){
    clear();selected=undefined;basis=undefined;pin=undefined;dropped=[];refill=false;
    for(const id of ['ai-agent-tools','ai-agent-roles'])get(id).replaceChildren();
    for(const [id,first] of [['ai-agent-model','Choose a model…'],['ai-agent-effort','Choose an effort…']])get(id).replaceChildren(new Option(first,''));
    for(const id of ['ai-agent-create-form','ai-agent-form'])form(id).reset();input('ai-agent-retire-confirm').checked=false;
    for(const id of ['ai-agent-title','ai-agent-facts','ai-agent-output-help','ai-agent-roles-note'])get(id).textContent='';
    for(const id of ['ai-agent-model-pin-note','ai-agent-stale','ai-agent-roles-note'])get(id).hidden=true;
  }

  function render(){
    if(!directory)return;const choices=directory;
    renderRegularTable(get('ai-agent-table'),'ai-agents',options.identity(),['Agent','State','Newest version','Published version','Published model','Last saved'],
      choices.agents.map(a=>{const [name,...cells]=agentRow(a,choices,instant);return [button(name,()=>select(a.id)),...cells];}),[],options.busy);
    renderRegularTable(get('ai-agent-audit'),'ai-agent-audit',options.identity(),['Saved','By','Agent','Change'],
      choices.audit.map(a=>[instant(a.at),a.actor,a.agent,agentChange(a.action)]),[],options.busy);
    const help=boundsHelp(choices);get('ai-agent-instructions-help').textContent=help.instructions;get('ai-agent-grant-help').textContent=help.grantDays;
    get('ai-agent-model-note').hidden=choices.models.length>0;
    const shown=agent();get('ai-agent-card').hidden=!shown;
    if(!shown)return;
    const scope=JSON.stringify([options.identity(),shown.id]),draft=shown.newest&&shown.newest.version>(shown.publishedVersion??0)?shown.newest:null;
    get('ai-agent-title').textContent=publicText(shown.name);
    get('ai-agent-facts').textContent=publicText([agentRow(shown,choices,instant)[1],`agent version ${shown.version}`,
      shown.published?`published version ${shown.published.version}`:'not published',...draft?[`draft version ${draft.version}`]:[]].join(' · '));
    renderRegularTable(get('ai-agent-published'),'ai-agent-published',scope,['Published version','Value'],shown.published?versionRows(shown.published,choices,instant):[],[],options.busy);
    renderRegularTable(get('ai-agent-draft'),'ai-agent-draft',scope,['Draft','Value'],draft?versionRows(draft,choices,instant):[],[],options.busy);
  }
  function sync(){
    const busy=options.busy(),shown=agent(),active=shown?.state==='active',held=!!pending.command||!directory;
    for(const id of ['ai-agent-create-form','ai-agent-form'])
      for(const node of form(id).querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement|HTMLButtonElement>('input,select,textarea,button'))node.disabled=busy||held;
    // A retired agent changes no more; Publish is offered once something newer than the published version exists.
    get('ai-agent-form').hidden=!active;
    const publish=get<HTMLButtonElement>('ai-agent-publish');publish.hidden=!active||!shown?.newest||shown.newest.version<=(shown.publishedVersion??0);publish.disabled=busy||held;
    const retire=get<HTMLButtonElement>('ai-agent-retire');retire.hidden=!active;retire.disabled=busy||held;
    input('ai-agent-retire-confirm').closest('label')!.hidden=!active;input('ai-agent-retire-confirm').disabled=busy||held;
    get('ai-agents-pending').hidden=!pending.command;
    // While a request is held the newer version may be its own, and the retained request says so.
    get('ai-agent-stale').hidden=!stale(shown)||!!pending.command;
    const body=pending.command?.body,named=directory?.agents.find(a=>a.id===body?.agentId)?.name;
    const subject=pending.command?.path.endsWith('/create')?`A create of the agent “${String(body?.name)}”`
      :`${{save:'A saved version of',publish:'A Publish of',retire:'A Retire of'}[pending.command?.path.split('/').at(-1) as 'save'|'publish'|'retire']??'A change to'} ${named?`“${named}”`:'an agent these settings cannot list now'}`;
    get('ai-agents-pending-text').textContent=pending.command?publicText(`${subject} has no confirmed outcome. Retry sends the same request: it reads the stored outcome if the change was saved, and otherwise makes it once. Discarding the request leaves any saved change in place.`):'';
  }
  async function load(){
    const request=++generation,identity=options.identity();
    // An open card keeps what was being entered across a reload, even after one that failed; only a confirmed save,
    // opening an agent or starting again refills it.
    const entered=entries();
    clear();
    let next:Directory;
    try{next=await options.api<Directory>('settings/ai/agents');}
    catch(error){if(request===generation&&(error as {code?:string}).code==='access_denied')wipe();throw error;}
    if(request!==generation||identity!==options.identity())return;
    directory=next;
    if(!directory.agents.some(a=>a.id===selected)){selected=undefined;input('ai-agent-retire-confirm').checked=false;}
    if(refill){refill=false;prefill();}else fillChoices(entered);
    render();sync();
  }
  // Once sent, a request keeps its identity until it is confirmed, refused before any write, or discarded.
  async function send(){
    const command=pending.command,identity=options.identity();if(!command)return;
    const previouslyUncertain=pending.sending();store();
    let answer:Agent;
    try{
      answer=await options.api<Agent>(command.path,{...command.body,operationId:command.operationId});
      if(identity!==options.identity()||pending.command!==command)return;
    }catch(error){
      if(identity!==options.identity()||pending.command!==command)return;
      const code=(error as {code?:string}).code;
      pending.failed(code,previouslyUncertain);store();
      // After an unconfirmed outcome the page shows what was actually saved; a denied read keeps nothing.
      if(code==='access_denied')wipe();else if(pending.command)await load().catch(()=>undefined);
      sync();
      const message=agentMessage(code);if(message)throw Object.assign(Error(message),{code});
      throw error;
    }
    pending.clear();store();
    const action=command.path.split('/').at(-1);
    // A confirmed save refills the open card from its newest version at the next successful read, even if this reload
    // fails; Publish and Retire keep the form, and a created agent opens afresh.
    if(action==='create')form('ai-agent-create-form').reset();else if(action==='save')refill=true;
    await load();
    if(action==='create')select(answer.id);
  }
  async function change(path:string,body:Record<string,unknown>){
    if(pending.command)throw Error('Retry or discard the retained request before making another change.');
    if(!directory)throw Error('Refresh AI settings to confirm your current access before making a change.');
    pending.begin(path,body);store();await send();
  }

  get('ai-agents-refresh').onclick=()=>void options.act(load);
  // Choosing a model pins its current capability version; an effort is chosen anew for it.
  get('ai-agent-model').addEventListener('change',()=>{
    const model=directory?.models.find(m=>m.id===value('ai-agent-model'));pin=model&&{id:model.id,version:model.version};
    get('ai-agent-model-pin-note').hidden=true;fillChoices({...entries(),effort:''});
  });
  get('ai-agent-restart').onclick=()=>{if(options.busy())return;prefill();render();sync();};
  form('ai-agent-create-form').onsubmit=event=>{event.preventDefault();void options.act(()=>change('settings/ai/agents/create',{name:value('ai-agent-name')}));};
  form('ai-agent-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const a=current();
    if(stale(a))throw Error('A newer version of this agent was saved after this form was filled. Start again from the newest version.');
    const request=agentSaveRequest({description:value('ai-agent-description'),instructions:value('ai-agent-instructions'),modelId:value('ai-agent-model'),
      reasoning:value('ai-agent-effort'),caching:value('ai-agent-caching'),tools:checked('ai-agent-tools'),allowedRoles:checked('ai-agent-roles'),
      limits:Object.fromEntries(limitFields.map(([key,id])=>[key,value(id)])) as AgentEntry['limits'],grantDays:value('ai-agent-grant-days')},directory!);
    await change('settings/ai/agents/save',{agentId:a.id,expectedVersion:a.version,...request});
  });};
  get('ai-agent-publish').onclick=()=>void options.act(async()=>{const a=current();await change('settings/ai/agents/publish',{agentId:a.id,expectedVersion:a.version});});
  get('ai-agent-retire').onclick=()=>void options.act(async()=>{
    const a=current();
    if(!input('ai-agent-retire-confirm').checked)throw Error('Confirm that retiring this agent is permanent.');
    await change('settings/ai/agents/retire',{agentId:a.id,expectedVersion:a.version});
  });
  get('ai-agents-retry').onclick=()=>void options.act(send);
  get('ai-agents-discard').onclick=()=>{if(options.busy())return;pending.clear();store();sync();};
  return {load,sync,
    refresh(){render();sync();},
    reset(){
      generation++;wipe();
      // This identity's unconfirmed request stays offered whether or not its directory can be read.
      pending.command=stored();sync();
    }};
}
