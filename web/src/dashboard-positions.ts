// The Positions widget: a saved risk run definition's latest completed run as a delta ladder by month, with Run now to queue a new one. The
// ladder is `deltaLadder` of the backend's parts and the choice of run, its freshness and any newer or incomplete run are the backend's;
// nothing is re-derived here. A definition is configured only from the books the page can read and the exact price units the risk
// catalog states for what its preset shocks, each with a size the person enters (never prefilled).
import {deltaLadder,type Ladder} from './delta-ladder.js';
import {curveName} from './market-view.js';
import {freshnessFooter,type Freshness} from './freshness.js';
import {PendingCommand} from './pending-command.js';
import {presetTargets,type Preset,type PresetCatalog} from './risk-presets.js';
import type {RiskReport} from './risk-report.js';
import type {ScenarioTarget} from './risk-scenarios.js';
import {node} from './tiles.js';
import {refusalNote,type DashboardMarket} from './dashboard-levels.js';
import type {WidgetContent} from './dashboard-layout.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type RunRef={runId:string;valuationAt:string};
export type IncompleteRun=RunRef&{failedParts:number[]};
/** The latest-run read as the backend answers it; a tie is its 409 refusal, read into the same shape. */
export type Latest=
  {state:'none'}|{state:'tie';runs:RunRef[]}|
  {state:'latest'|'newer_available';run:{runId:string;version:number;valuationAt:string;knownAt:string;parts:{result?:Pick<RiskReport,'factors'>}[]};newer:RunRef|null;incomplete:IncompleteRun|null};
export type PositionsView=
  {ladder:Ladder;freshness:Freshness;newer:RunRef|null;incomplete:IncompleteRun|null}|{none:true}|{tie:RunRef[]}|{refusal:string};
type SizeUnit={label:string;priceUnit:unknown};
type Definition={definitionId:string;version:number;name:string;preset:'held_contracts'|'held_curves';bookIds:string[]};
type Directory={context:{contextId:string};books:{id:string;name:string}[]};
type Context={snapshotId:string;convention:string;knowledgeMode:'retained';contextId:string};

export const noRunYet='No completed run yet';
// The presets a definition can store and the catalog preset each shocks.
const presets:Record<Definition['preset'],{label:string;catalog:Preset}>={held_contracts:{label:'Shock every held contract',catalog:'held'},held_curves:{label:'Parallel shift',catalog:'parallel'}};
const missingResult='A part of the latest run has no result, so its ladder is not shown. Run it again.';

/** Run now is offered in working data, not in a historical context, which is read only. */
export const runNowOffered=(historical:boolean)=>!historical;

/** The widget's content for the latest read. A run that cannot be drawn exactly as returned is a refusal, never a partial ladder. */
export function positionsView(latest:Latest,historical=false):PositionsView{
  if(latest.state==='none')return {none:true};
  if(latest.state==='tie')return {tie:latest.runs};
  try{
    const reports=latest.run.parts.map(part=>part.result??(()=>{throw Error(missingResult);})());
    return {ladder:deltaLadder(reports,'delta',curveName),freshness:{state:historical?'historical':latest.state,observedAt:latest.run.valuationAt,source:'Risk run'},
      newer:latest.newer,incomplete:latest.incomplete};
  }catch(error){return {refusal:error instanceof Error?error.message:'The latest run could not be shown.'};}
}

/** The latest completed run of a definition at a retained context; completed runs tied for the latest are read into a tie, any other refusal is thrown. */
export async function readLatest(api:Api,definitionId:string,context:unknown):Promise<Latest>{
  try{return await api<Latest>(`risk/definitions/${definitionId}/latest`,{context});}
  catch(error){
    const {code,details}=error as {code?:string;details?:{runs?:RunRef[]}};
    if(code==='risk_run_tie'&&Array.isArray(details?.runs))return {state:'tie',runs:details.runs};
    throw error;
  }
}

/** The price units a size is asked for: each exact unit the targets state, once, in the order they first name it. The label is only for display. */
export function sizeUnits(targets:readonly Pick<ScenarioTarget,'unit'|'priceUnit'>[]):SizeUnit[]{
  const units=new Map<string,SizeUnit>();
  for(const {unit,priceUnit} of targets){
    if(!priceUnit)throw Error('The risk catalog does not state the exact price unit of a target. Reload it.');
    const key=JSON.stringify(priceUnit);
    if(!units.has(key))units.set(key,{label:unit,priceUnit});
  }
  return [...units.values()];
}

/** The terms a definition is saved with: a size for every unit, entered by the person; a missing one is refused, never filled in. */
export function definitionTerms(input:{name:string;bookIds:string[];preset:Definition['preset'];units:readonly SizeUnit[];sizes:readonly string[]}){
  const name=input.name.trim();
  if(!name)throw Error('Name the definition.');
  if(!input.bookIds.length)throw Error('Choose at least one book.');
  if(!input.units.length)throw Error('Read the price units the preset shocks, then enter a size for each.');
  const sizes=input.units.map((unit,index)=>{
    const size=input.sizes[index]?.trim();
    if(!size)throw Error(`Enter a shock size for ${unit.label}.`);
    return {unit:unit.priceUnit,size};
  });
  return {name,bookIds:input.bookIds,preset:input.preset,sizes};
}

/** Each amount as a share of the largest magnitude among them (missing cells draw no bar): a visual scale only; the amounts themselves stay the backend's. */
export function barWidths(amounts:readonly (string|null)[]):number[]{
  const size=amounts.map(amount=>amount===null?0:Math.abs(Number(amount))),largest=Math.max(0,...size);
  return size.map(value=>largest===0?0:value/largest);
}

/** The retained context the page is reading at, for the catalog, Run now and the latest read, and the books readable there. */
async function retainedContext(market:DashboardMarket,api:Api):Promise<{context:Context;books:Directory['books']}>{
  const selected=market.navigation.context,request={snapshotId:market.snapshotId,convention:'trading_workspace_v1',
    knowledgeMode:selected?'retained':'current',...(selected?{contextId:selected.contextId}:{})};
  const {context,books}=await api<Directory>('trading/directory',request);
  return {context:{...request,knowledgeMode:'retained',contextId:context.contextId},books};
}

const knownRejections=new Set(['risk_definition','risk_definition_conflict','risk_definition_request_conflict','risk_definition_size','risk_definition_targets','risk_definition_version',
  'risk_job_context','access_denied','request_format','request_too_large','request_token']);
const messageOf=(error:unknown)=>error instanceof Error?error.message:'The operation could not be completed.';

function ladderElement(ladder:Ladder){
  const wrap=node('div','positions-ladder'),columns=ladder.columns;
  const bars=columns.map((_,index)=>barWidths(ladder.groups.flatMap(g=>g.rows).map(row=>{const cell=row.cells[index];return cell.missing?null:cell.exact;})));
  let seen=0;
  for(const group of ladder.groups){
    const table=node('table','positions-table'),head=node('thead'),titles=node('tr');
    titles.append(node('th','','Period'),node('th','','Coordinate'),node('th','','Shock'),...columns.map(c=>node('th','positions-measure',c.title)));
    head.append(titles);table.append(node('caption','',group.title),head);
    const rows=node('tbody');
    for(const row of group.rows){
      const line=node('tr'),at=seen++;
      line.append(node('td','',row.period),node('td','',row.coordinate),node('td','',row.shock));
      row.cells.forEach((cell,index)=>{
        const td=node('td','positions-cell');
        if(cell.missing){td.textContent='–';td.title='The run returned no total for this book and measure.';}
        else{
          const bar=node('span','positions-bar'),negative=cell.exact.startsWith('-');
          bar.style.width=`${bars[index][at]*100}%`;bar.dataset.sign=negative?'negative':'positive';
          td.title=`${cell.exact} ${cell.unit}`;td.append(bar,node('span','positions-figure',cell.text));
        }
        line.append(td);
      });
      rows.append(line);
    }
    table.append(rows);wrap.append(table);
  }
  return wrap;
}

function viewElement(view:PositionsView,zone:string){
  const body=node('div');
  if('refusal' in view)return refusalNote(Error(view.refusal));
  if('none' in view){body.append(node('p','muted',noRunYet));return body;}
  if('tie' in view){
    body.append(node('p','',`Completed runs share the latest valuation time, so none is shown. ${view.tie.length} runs are tied:`));
    const list=node('ul');
    for(const run of view.tie)list.append(node('li','',`Run ${run.runId} · valued ${run.valuationAt}`));
    body.append(list);return body;
  }
  body.append(ladderElement(view.ladder),node('p','fine-print',freshnessFooter(view.freshness,zone)));
  if(view.newer)body.append(node('p','positions-note',`A newer completed run is available: ${view.newer.runId}, valued ${view.newer.valuationAt}.`));
  if(view.incomplete)body.append(node('p','positions-note',`A newer run did not complete: ${view.incomplete.runId}, valued ${view.incomplete.valuationAt}; part ${view.incomplete.failedParts.join(', ')} failed or was cancelled.`));
  return body;
}

export function setupPositions(api:Api){
  // The request a Run now sends is kept per definition until its outcome is known, so a retry after a lost reply carries the same request id.
  const runs=new Map<string,PendingCommand>();
  const pendingRun=(definitionId:string)=>runs.get(definitionId)??runs.set(definitionId,new PendingCommand(code=>!!code&&knownRejections.has(code))).get(definitionId)!;

  function body(config:Extract<WidgetContent,{kind:'positions'}>['config'],market:DashboardMarket):HTMLElement{
    const host=node('div','positions'),content=node('div'),status=node('p','field-help'),run=node('button','secondary dashboard-edit','Run now') as HTMLButtonElement;
    status.setAttribute('role','status');run.type='button';run.hidden=!runNowOffered(market.historical);
    host.append(content,status,run);
    let definition:Definition|undefined;
    async function load(){
      content.replaceChildren(node('p','muted','Reading the latest run…'));
      try{
        definition=(await api<Definition[]>('risk/definitions')).find(d=>d.definitionId===config.definitionId);
        if(!definition)throw Error('This definition is no longer available. Remove the widget and add it again.');
        const {context}=await retainedContext(market,api);
        content.replaceChildren(node('h4','',definition.name),viewElement(positionsView(await readLatest(api,definition.definitionId,context),market.historical),market.zone));
      }catch(error){content.replaceChildren(refusalNote(error));}
    }
    run.onclick=async()=>{
      if(!definition)return;
      const pending=pendingRun(definition.definitionId);
      run.disabled=true;status.textContent='Queuing the run…';
      try{
        const command=pending.command??pending.begin(`risk/definitions/${definition.definitionId}/run`,{version:definition.version,context:(await retainedContext(market,api)).context});
        const previouslyUncertain=pending.sending();
        try{
          const queued=await api<{runId:string;jobs:unknown[]}>(command.path,{...command.body,requestId:command.operationId});
          pending.clear();status.textContent=`Run ${queued.runId} is queued in ${queued.jobs.length} part${queued.jobs.length===1?'':'s'}. Reload the widget when it has finished.`;
        }catch(error){
          pending.failed((error as {code?:string}).code,previouslyUncertain);
          status.textContent=pending.command?`${messageOf(error)} Run now again to retry the same request.`:messageOf(error);
        }
      }catch(error){status.textContent=messageOf(error);}
      finally{run.disabled=false;}
    };
    const reload=node('button','secondary','Reload') as HTMLButtonElement;reload.type='button';reload.onclick=()=>void load();host.append(reload);
    void load();
    return host;
  }

  /**
   * The configuration of a Positions widget: an existing definition, or a new one built from the books the page can read, a preset and a
   * size for each exact price unit the risk catalog states for what the preset shocks. `settle` saves a new definition once, under one request id
   * kept until its outcome is known, and answers the widget's content.
   */
  function configurator(market:()=>DashboardMarket){
    const saving=new PendingCommand(code=>!!code&&knownRejections.has(code));
    const element=node('div','dashboard-add-positions'),status=node('p','field-help');status.setAttribute('role','status');
    const field=<T extends HTMLElement>(text:string,control:T)=>{const row=node('label','',text);row.append(control);element.append(row);return control;};
    const make=<K extends keyof HTMLElementTagNameMap>(tag:K)=>{const control=node(tag,'dashboard-edit');return control;};
    const existing=field('Definition',make('select')),name=field('Name',make('input')),books=field('Books',make('select')),preset=field('Preset',make('select')),
      read=node('button','secondary dashboard-edit','Read price units') as HTMLButtonElement,sizes=node('div','dashboard-add-sizes');
    books.multiple=true;read.type='button';
    for(const [value,{label}] of Object.entries(presets))preset.add(new Option(label,value));
    element.append(read,sizes,status);
    let units:SizeUnit[]=[];
    const fresh=()=>existing.value==='';
    const inputs=()=>[...sizes.querySelectorAll<HTMLInputElement>('input')];
    function show(){for(const row of [name,books,preset].map(control=>control.parentElement!))row.hidden=!fresh();read.hidden=sizes.hidden=!fresh();}
    function invalidate(){units=[];sizes.replaceChildren();}
    existing.onchange=show;books.onchange=invalidate;preset.onchange=invalidate;
    read.onclick=async()=>{
      invalidate();status.textContent='Reading the price units…';
      try{
        const bookIds=[...books.selectedOptions].map(option=>option.value);
        if(!bookIds.length)throw Error('Choose at least one book.');
        const {context}=await retainedContext(market(),api),catalog=await api<PresetCatalog>('portfolio/risk/catalog',{context,bookIds,portfolioIds:[]});
        units=sizeUnits(presetTargets(catalog,presets[preset.value as Definition['preset']].catalog));
        for(const unit of units){const input=make('input');input.setAttribute('aria-label',`Shock size for ${unit.label}`);input.inputMode='decimal';
          const row=node('label','',`Shock size for ${unit.label}`);row.append(input);sizes.append(row);}
        status.textContent=units.length?'Enter a shock size for each price unit.':'The preset shocks nothing these books hold.';
      }catch(error){status.textContent=messageOf(error);}
    };
    /** Offers the saved definitions and the books readable now; called when the page loads or its context refreshes. */
    async function refresh(){
      status.textContent='';
      try{
        const [definitions,{books:readable}]=await Promise.all([api<Definition[]>('risk/definitions'),retainedContext(market(),api)]);
        const chosen=existing.value;
        existing.replaceChildren(new Option('New definition',''),...definitions.map(d=>new Option(d.name,d.definitionId,false,d.definitionId===chosen)));
        books.replaceChildren(...readable.map(b=>new Option(b.name,b.id)));invalidate();show();
      }catch(error){status.textContent=messageOf(error);}
    }
    async function settle():Promise<WidgetContent>{
      if(!fresh())return {kind:'positions',config:{definitionId:existing.value}};
      const terms=definitionTerms({name:name.value,bookIds:[...books.selectedOptions].map(o=>o.value),preset:preset.value as Definition['preset'],units,sizes:inputs().map(i=>i.value)});
      const command=saving.begin('risk/definitions',{definitionId:null,expectedVersion:null,...terms}),previouslyUncertain=saving.sending();
      try{
        const saved=await api<Definition>(command.path,{...command.body,requestId:command.operationId});
        saving.clear();return {kind:'positions',config:{definitionId:saved.definitionId}};
      }catch(error){saving.failed((error as {code?:string}).code,previouslyUncertain);throw error;}
    }
    show();
    return {element,refresh,settle};
  }
  return {body,configurator,reset:()=>runs.clear()};
}
