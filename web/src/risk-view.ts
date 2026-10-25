// The risk report of the Blotter desk (blotter-desk.md, Side panel: Risk report), drawn in the side panel's Risk report mode. It offers
// Shock every held contract and Parallel shift, which shock the targets the catalog names for what the books hold, and the custom
// scenario lab with nested stresses; it runs a report now or queues it as jobs, keeps the retained job history and shows a run's results.
// A run of more than sixteen shocks is split into parts of at most sixteen at the one retained context of its catalog, run one after
// another or queued as one job each, and shown as one run. Each factor's results are its own, so no arithmetic combines parts, and a
// part that fails is said to have failed. What the report holds outlives the panel's other modes; a new read of the Blotter clears it.
import {riskContextLabel} from './risk-time.js';
import {riskTotalGroups,riskWorkDetails,type RiskValues,type RiskTotal,type RiskWorkReport} from './risk-report.js';
import {PendingCommand} from './pending-command.js';
import {renderDataGrid} from './data-grid.js';
import {renderChart,clearChart} from './chart-view.js';
import {publicText,timeLabels} from './market-view.js';
import {scenarioShift,knownRiskRejection,type ScenarioTarget,type ScenarioShift} from './risk-scenarios.js';
import {presetGaps,presetRequest,presetTargets,runParts,sizesByUnit,splitBumps,type Preset} from './risk-presets.js';
import {valuationReason,type ValuationReason} from './valuation-status.js';
import {trackEdits} from './edit-tracking.js';
import type {SidePanel} from './side-panel.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Factor={id:string;label:string;coordinate:string;kind:string;unit:string};
type FinancialContext={contextId:string;effectiveOn:string;knownAt?:string;knownThrough:number};
type Excluded={tradeId:string;bindingId:string;bookId:string;tradeReference:string;instrumentType:string;reason:ValuationReason};
type Catalog={pricingPolicy:unknown;scenarioTargets:ScenarioTarget[];heldContractTargets:string[];heldCurveTargets:string[];
  heldContractsWithoutTarget:string[];heldCurvesWithoutTarget:string[];excludedPositions:Excluded[];bumpConvention:string;stressConvention:string;
  convention:string;numericalPolicy:string;context:FinancialContext;asOf:string};
type Scope={context:unknown;bookIds:string[];portfolioIds:string[]};
type StressLayer={id:string;shift:ScenarioShift;label:string;unit:string};
type Request=Scope&{pricingPolicy:unknown;bumps:ScenarioShift[];stress:ScenarioShift[];convention:string;numericalPolicy:string};
type JobState='queued'|'running'|'completed'|'failed'|'cancellation_requested'|'cancelled';
type Job={id:string;requestId?:string;state:JobState;error?:{code:string;message:string};result?:Report;context?:FinancialContext;asOf?:string};
type Position={book:string;tradeReference:string;instrumentReference:string;instrumentType:string;measure:string;measureLabel:string;values:RiskValues};
type Report=RiskWorkReport&{displayPolicy:unknown;scenario:{depth:number;isBase:boolean};context:{contextId:string};snapshotId:string;
  excludedPositions?:Excluded[];factors:{factor:Factor;bump:string;totals:RiskTotal[];positions:Position[]}[]};
/**
 * A part of a run and what became of it: the report it returned, or the job it was queued as, or why it failed. A queued part's
 * submission is kept as its `recovery` until its outcome is known. A retained job read from the history has no request of its own.
 */
type Part={request?:Request;report?:Report;job?:Job;failure?:string;recovery?:PendingCommand};
type Choice=Preset|'custom';
/**
 * What a run was set up as: its report, its shock size for each price unit, and the held prices or curves the preset did not shock,
 * as no whole target covers them. A custom scenario's sizes are its factors' own.
 */
type RunSetup={report:Choice;label:string;sizes:Record<string,string>;withoutTarget:string[]};
/**
 * A run on screen: run now or queued, its parts in the order of its shocks, the context it was made at, and its setup, which a retained
 * job read from the history does not record.
 */
type Run={queued:boolean;parts:Part[];context?:FinancialContext;asOf?:string;setup?:RunSetup};
/**
 * The person's choices in the setup, carried over to a catalog read at changed inputs: the report, the sizes, the custom scenario's own
 * fields and its stresses, and each chosen scenario target by the catalog's id, with its label to name it by when it is no longer offered.
 */
type Choices={choice:Choice|null;sizes:Map<string,{value:string;prefilled:boolean}>;fields:Record<string,string>;stressOpen:boolean;
  targets:Record<'risk'|'risk-stress',{id:string;label:string}|null>;stress:StressLayer[]};

/** Why an account without the Risk Analyst role cannot run a scenario. */
export const riskDenied='Scenario execution requires the Risk Analyst role.';
const jobLabels:Record<JobState,string>={queued:'Queued',running:'Running',completed:'Completed',failed:'Failed',cancellation_requested:'Cancellation requested',cancelled:'Cancelled'};
const active=(job:Job|undefined)=>!!job&&['queued','running','cancellation_requested'].includes(job.state);
const failure=(error:unknown,fallback:string)=>error instanceof Error&&error.message?error.message:fallback;
const code=(error:unknown)=>(error as {code?:string}|undefined)?.code;
const plural=(count:number,word:string)=>`${count} ${word}${count===1?'':'s'}`;
const partsText=(indexes:number[],total:number)=>`${indexes.length===1?'part':'parts'} ${indexes.map(i=>i+1).join(', ')} of ${total}`;
const presetText:Record<Preset,{label:string;noun:string;none:string;each:string;gaps:string;complete:string;left:string}>={
  held:{label:'Shock every held contract',noun:'held contract',none:'The selected books hold no contract this preset can shock.',each:'each shocked up and down on its own',
    gaps:'No whole-contract scenario target covers these held prices, so this preset does not shock them:',complete:'every held price shocked',
    left:'not shocked, as no whole-contract target covers'},
  parallel:{label:'Parallel shift',noun:'held curve',none:'The selected books hold no curve this preset can shift.',each:'each shifted up and down as a whole',
    gaps:'No whole-curve scenario target covers these held curves, so this preset does not shift them:',complete:'every held curve shifted',
    left:'not shifted, as no whole-curve target covers'},
};
// The custom scenario's own fields, carried over with the person's other choices when the inputs on screen change.
const customFields=['risk-coordinate','risk-target-kind','risk-from','risk-to','risk-bump','risk-stress-coordinate','risk-stress-target-kind','risk-stress-from','risk-stress-to','risk-stress-size'];
/** What a run's results and download say it was set up as, so that a run that left prices out can be told from a complete one. */
function setupText(setup:RunSetup|undefined):string{
  if(!setup)return 'A retained job: the report it was run as is not recorded with it, and each factor below states its shock.';
  if(setup.report==='custom')return setup.label;
  const text=presetText[setup.report],sizes=Object.entries(setup.sizes).map(([unit,size])=>`±${size} ${unit}`).join(', ');
  const left=setup.withoutTarget;
  return `${setup.label} · ${sizes} · ${left.length?`${text.left} ${left.length===1?'it':'them'}: ${left.join('; ')}`:text.complete}`;
}

/**
 * The risk report over its markup, `#risk-report`, which waits on the editor shelf until the side panel's Risk report mode `mount`s it
 * and goes back there when the mode is left. `scope` is the books and context of the Blotter read on screen; `edited` hears the setup
 * start or stop holding the person's edits, which the panel asks about before they are left; `onChange` hears whatever the report
 * shows change, however it changed: an action, a part that finished on the report's own timer, or a reset.
 */
export function setupRisk(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;canRun:()=>boolean;profile:()=>string;
  scope:()=>Scope;onDenied:()=>void;onChange:()=>void;edited?:()=>void}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const root=get('risk-report'),shelf=root.parentElement!,form=get<HTMLFormElement>('risk-form');
  const select=get<HTMLSelectElement>('risk-factor'),bump=get<HTMLInputElement>('risk-bump'),stressFactor=get<HTMLSelectElement>('risk-stress-factor'),stressSize=get<HTMLInputElement>('risk-stress-size');
  const currentScopeText=get('risk-scope').textContent;
  // The person's own choices and entries in the setup, which leaving it would lose. Prefilled sizes are not theirs until they change one.
  const edits=trackEdits(form,[],()=>{options.edited?.();});
  let catalog:Catalog|undefined,scope:Scope|undefined,stress:StressLayer[]=[],reading=false,unread:string|undefined;
  // The person's choices carried over from a setup the inputs on screen changed under, until a catalog read at the new inputs takes them.
  let carried:Choices|undefined;
  // The size entered for each price unit, and whether it is the one last used, prefilled and not yet changed.
  let sizes=new Map<string,{value:string;prefilled:boolean}>();
  // Queued runs whose submission has no known outcome for some part, by account: they are reconciled before another is queued.
  const submissions=new Map<string,Run>();
  let generation=0,pollTimer:ReturnType<typeof setTimeout>|undefined,run:Run|undefined,reviewing=false,recent:Job[]=[];
  // A run the person has just started is brought into view in the panel: its jobs once queued, and its results once they are shown.
  let revealResults=false;
  let grids:ReturnType<typeof renderDataGrid>[]=[],charts:HTMLElement[]=[];
  // Factor results drawn when their section is open and the report is in the panel: a grid needs a visible width.
  let draws:(()=>void)[]=[];
  const mounted=()=>!root.hidden;
  const running=()=>!!run&&run.parts.some(p=>active(p.job));
  const uncertain=(current:Run|undefined)=>current?.parts.flatMap((p,i)=>p.recovery?.command?[i]:[])??[];
  const complete=(current:Run|undefined)=>!!current&&current.parts.every(p=>p.report);
  // The setup holds the person's edits: typed or chosen, or carried over from inputs that changed under it.
  const edited=()=>edits.edited()||carried!==undefined;
  // Keeps choices to carry over, and says so to whoever hears the setup start or stop holding edits.
  function carry(kept:Choices|undefined){carried=kept;options.edited?.();}
  function stopPolling(){clearTimeout(pollTimer);pollTimer=undefined;}

  // --- what the report shows -------------------------------------------------------------------------------------------------
  function clearResults(){
    for(const grid of grids)grid.destroy();grids=[];for(const chart of charts)clearChart(chart);charts=[];draws=[];
    get('risk-results').replaceChildren();get('risk-work').replaceChildren();get('risk-export').hidden=true;get('risk-export-note').hidden=true;
  }
  function clearJob(){
    get('risk-job').hidden=true;for(const id of ['risk-job-context','risk-job-scenario','risk-job-status'])get(id).textContent='';get('risk-job-parts').replaceChildren();
    get('risk-job-return').hidden=true;get<HTMLSelectElement>('risk-job-history').value='';get('risk-scope').textContent=currentScopeText;
  }
  /** Forgets the run on screen, its results and its jobs: the setup stays. */
  function forgetRun(){stopPolling();run=undefined;reviewing=false;revealResults=false;clearResults();clearJob();get('risk-progress').textContent='';}
  function reveal(id:string){if(mounted())get(id).scrollIntoView({block:'start'});}
  function resetControls(){
    select.replaceChildren(new Option('Choose a scenario target…',''));stressFactor.replaceChildren(new Option('Choose a scenario target…',''));
    get('risk-job-history').replaceChildren(new Option('Choose a retained job…',''));bump.value='';stressSize.value='';get('risk-unit').textContent='';get('risk-stress-unit').textContent='';
    get<HTMLDetailsElement>('risk-stress').open=false;
    for(const prefix of ['risk','risk-stress']){get<HTMLSelectElement>(prefix+'-coordinate').value='all';get<HTMLSelectElement>(prefix+'-target-kind').value='all';get(prefix+'-bucket').hidden=true;
      for(const suffix of ['from','to']){const input=get<HTMLInputElement>(prefix+'-'+suffix);input.value='';input.required=false;input.disabled=true;}}
    for(const choice of choices())choice.checked=false;
  }
  /** Forgets everything the report holds but the submissions whose outcome is unknown, which are kept until they are settled. */
  function reset(){
    generation++;forgetRun();catalog=undefined;scope=undefined;stress=[];recent=[];reading=false;unread=undefined;sizes=new Map();carried=undefined;
    get('risk-carried').hidden=true;get('risk-carried').textContent='';
    resetControls();edits.clear();renderStress();showChoice();describe();sync();
  }
  function denied(error:unknown){if(code(error)==='access_denied'){reset();options.onDenied();}}
  async function call<T>(path:string,body?:unknown):Promise<T>{try{return await options.api<T>(path,body);}catch(error){clearResults();denied(error);throw error;}}
  const factorLabel=(f:Factor|ScenarioTarget)=>publicText(f.label);

  // --- the setup ---------------------------------------------------------------------------------------------------------------
  const choices=()=>Array.from(form.querySelectorAll<HTMLInputElement>('input[name=risk-choice]'));
  const chosen=():Choice|null=>(choices().find(c=>c.checked)?.value as Choice|undefined)??null;
  // The last sizes used, by price unit, are prefilled for the person to see; without session storage the sizes start empty.
  const sizesKey=()=>`openetrm.desk.shockSizes.${options.profile()}`;
  function storedSizes():Map<string,string>{
    try{
      const value=JSON.parse(sessionStorage.getItem(sizesKey())??'null');
      if(value&&typeof value==='object'&&!Array.isArray(value))
        return new Map(Object.entries(value as Record<string,unknown>).flatMap(([unit,size])=>typeof size==='string'&&size.trim()&&size.length<=45?[[unit,size] as const]:[]));
    }catch{/* A prefill is a convenience: an unreadable one prefills nothing. */}
    return new Map();
  }
  function storeSizes(used:Record<string,string>){
    try{sessionStorage.setItem(sizesKey(),JSON.stringify({...Object.fromEntries(storedSizes()),...used}));}catch{/* The run goes on; its sizes are not prefilled next time. */}
  }
  // A preset's targets and what it leaves out, or why the catalog cannot say them.
  function preset(kind:Preset):{targets:ScenarioTarget[];gaps:string[]}|{error:string}{
    try{return {targets:presetTargets(catalog!,kind),gaps:presetGaps(catalog!,kind)};}catch(error){return {error:failure(error,'The risk catalog is inconsistent. Reload it.')};}
  }
  /** What each choice of report offers at this read, said beside it. */
  function describe(){
    for(const kind of ['held','parallel'] as const){
      const shown=catalog?preset(kind):undefined,text=presetText[kind];
      get(`risk-choice-${kind}-detail`).textContent=!shown?'':'error' in shown?shown.error:shown.targets.length?`${plural(shown.targets.length,text.noun)} · ${text.each}`:text.none;
    }
    // A catalog is read only with its list of the positions a run leaves out: without one there is nothing to say.
    const excluded=catalog?catalog.excludedPositions:[];
    get('risk-excluded').hidden=!excluded.length;
    get('risk-excluded').textContent=excluded.length?publicText(`This close cannot value ${plural(excluded.length,'position')}, so a run leaves ${excluded.length===1?'it':'them'} out: ${excluded.map(e=>`${e.tradeReference} (${valuationReason(e.reason,e.instrumentType)})`).join('; ')}.`):'';
  }
  // Why the setup offers nothing to run, while it does not: its catalog is being read, could not be read, or was cleared with the read it belonged to.
  function describeStatus(){
    const status=get('risk-status'),missing=!catalog&&!reviewing;
    status.hidden=!reading&&!missing;
    status.textContent=reading?'Reading what the selected books hold…':missing?unread??'The risk report has no catalog for the Blotter on screen.':'';
    get('risk-reload').hidden=reading||!missing;
  }
  /** Shows the part of the setup the chosen report needs, and nothing of the others, which then take no part in the form. */
  function showChoice(){
    const choice=chosen(),presetShown=choice==='held'||choice==='parallel';
    const part=get<HTMLFieldSetElement>('risk-preset'),custom=get<HTMLFieldSetElement>('risk-custom');
    part.hidden=part.disabled=!presetShown;custom.hidden=custom.disabled=choice!=='custom';
    if(presetShown)drawPreset(choice);else get('risk-sizes').replaceChildren();
  }
  function drawPreset(kind:Preset){
    const shown=preset(kind),text=presetText[kind],targets='error' in shown?[]:shown.targets,gaps='error' in shown?[]:shown.gaps;
    const parts=splitBumps(targets).length;
    get('risk-preset-summary').textContent='error' in shown?shown.error:!targets.length?text.none
      :`${plural(targets.length,text.noun)}, ${text.each} by the size entered for its price unit.${parts>1?` The run is made in ${parts} parts of at most 16 shocks, at one market context.`:''}`;
    get('risk-preset-gaps').hidden=!gaps.length;get('risk-preset-gaps').textContent=gaps.length?publicText(`${text.gaps} ${gaps.join('; ')}.`):'';
    get('risk-sizes').replaceChildren(...sizesByUnit(targets).map((unit,index)=>{
      const entry=sizes.get(unit)??{value:'',prefilled:false};sizes.set(unit,entry);
      const field=document.createElement('div'),label=document.createElement('label'),input=document.createElement('input'),note=document.createElement('span');
      field.className='risk-size';input.id=`risk-size-${index}`;input.dataset.unit=unit;input.type='text';input.inputMode='decimal';input.required=true;input.maxLength=45;input.autocomplete='off';
      input.placeholder='Enter a positive size';input.value=entry.value;label.htmlFor=input.id;label.textContent=publicText(`Shock size · ${unit}`);
      note.id=`risk-size-${index}-note`;note.className='risk-prefilled';note.textContent='Prefilled · the size you last used';note.hidden=!entry.prefilled;
      if(entry.prefilled)input.setAttribute('aria-describedby',note.id);
      input.oninput=()=>{sizes.set(unit,{value:input.value,prefilled:false});note.hidden=true;input.removeAttribute('aria-describedby');edit();};
      field.append(label,input,note);return field;
    }));
  }
  async function open(){
    const retained=submissions.get(options.profile());
    // A run whose submission has no known outcome is shown under the context it was captured at until it is settled.
    if(retained){
      if(run!==retained){forgetRun();run=retained;}
      reviewing=true;const version=generation;showRun();sync();await recentJobs(version);return;
    }
    // A catalog read for the Blotter on screen stands with the setup and run it holds: any new read of the Blotter resets the report.
    if(catalog){sync();return;}
    // A new read keeps the choices carried over from inputs that changed, and puts them back over the catalog it reads.
    const kept=carried;reset();carry(kept);const version=generation;reading=true;describe();sync();
    try{
      scope=structuredClone(options.scope());
      const loaded=await call<Catalog>('portfolio/risk/catalog',scope);if(version!==generation)return;
      // A catalog that does not say which positions a run leaves out is refused, never read as saying none.
      if(!Array.isArray(loaded.excludedPositions))throw Error('The risk catalog is inconsistent. Reload it.');
      catalog=loaded;
      scope={...scope,context:{...(scope.context as Record<string,unknown>),knowledgeMode:'retained',contextId:catalog.context.contextId}};
      sizes=new Map([...storedSizes()].map(([unit,value])=>[unit,{value,prefilled:true}]));
      for(const prefix of ['risk','risk-stress'])targetChoices(prefix);
      if(carried)restore(carried);
      await recentJobs(version);
      if(version===generation)carried=undefined;
    }catch(error){if(version===generation){scope=undefined;catalog=undefined;unread='The risk catalog could not be read.';}throw error;}
    finally{if(version===generation){reading=false;describe();showChoice();sync();}}
  }
  /** The person's choices in the setup as they stand, to carry over to a catalog read at changed inputs. */
  function choicesNow():Choices{
    const target=(prefix:'risk'|'risk-stress')=>{const found=catalog?.scenarioTargets.find(t=>t.id===get<HTMLSelectElement>(prefix+'-factor').value);return found?{id:found.id,label:found.label}:null;};
    return {choice:chosen(),sizes:new Map(sizes),fields:Object.fromEntries(customFields.map(id=>[id,get<HTMLInputElement|HTMLSelectElement>(id).value])),
      stressOpen:get<HTMLDetailsElement>('risk-stress').open,targets:{risk:target('risk'),'risk-stress':target('risk-stress')},stress:[...stress]};
  }
  /**
   * Puts the person's choices back over a catalog read at changed inputs, which they stay the person's edits in, and names what it no
   * longer offers: a chosen scenario target or stress, or a size typed for a price unit no target of the chosen preset is quoted in.
   */
  function restore(kept:Choices){
    const offered=new Set(catalog!.scenarioTargets.map(t=>t.id)),gone:string[]=[];
    for(const input of choices())input.checked=input.value===kept.choice;
    sizes=new Map([...sizes,...kept.sizes]);
    for(const [id,value] of Object.entries(kept.fields))get<HTMLInputElement|HTMLSelectElement>(id).value=value;
    get<HTMLDetailsElement>('risk-stress').open=kept.stressOpen;
    for(const prefix of ['risk','risk-stress'] as const){
      targetChoices(prefix);
      const target=kept.targets[prefix],control=get<HTMLSelectElement>(prefix+'-factor');
      if(!target)continue;
      control.value=offered.has(target.id)?target.id:'';
      if(control.value===target.id)targetDetails(prefix);else gone.push(target.label);
    }
    stress=kept.stress.filter(layer=>{if(offered.has(layer.id))return true;gone.push(layer.label);return false;});
    if(kept.choice==='held'||kept.choice==='parallel'){
      const shown=preset(kept.choice),units='error' in shown?[]:sizesByUnit(shown.targets);
      for(const [unit,entry] of kept.sizes)if(!entry.prefilled&&entry.value.trim()&&!units.includes(unit))gone.push(`the shock size for ${unit}`);
    }
    const line=get('risk-carried');line.hidden=false;
    line.textContent=publicText(`The inputs on screen changed, so this setup was read again for them with your choices.${gone.length?` No longer offered, so left out: ${gone.join('; ')}.`:''}`);
    renderStress();edits.mark();
  }
  function bucket(prefix:string){return {from:get<HTMLInputElement>(prefix+'-from').value,to:get<HTMLInputElement>(prefix+'-to').value};}
  function targetChoices(prefix:string){
    const control=get<HTMLSelectElement>(prefix+'-factor'),coordinate=get<HTMLSelectElement>(prefix+'-coordinate').value,kind=get<HTMLSelectElement>(prefix+'-target-kind').value;
    const offered=catalog?.scenarioTargets.filter(t=>(coordinate==='all'||t.coordinate===coordinate)&&(kind==='all'||t.kind===kind))??[];
    control.replaceChildren(new Option('Choose a scenario target…',''),...offered.map(t=>new Option(factorLabel(t),t.id)));targetDetails(prefix);
  }
  function targetDetails(prefix:string){
    const target=catalog?.scenarioTargets.find(t=>t.id===get<HTMLSelectElement>(prefix+'-factor').value),bucketed=target?.kind==='delivery_bucket';
    get(prefix+'-bucket').hidden=!bucketed;for(const suffix of ['from','to']){const input=get<HTMLInputElement>(prefix+'-'+suffix);input.required=prefix==='risk'&&!!bucketed;input.disabled=!bucketed;}
    get(prefix+'-unit').textContent=target?`${target.coordinate==='market_quote'?'Primitive market quote':'Computed curve output'} · ${target.unit}. ${prefix==='risk'?'Symmetric up/down repricing around the selected scenario.':'Signed additive offset to the parent scenario.'}`:'';
  }
  for(const prefix of ['risk','risk-stress']){
    for(const field of ['coordinate','target-kind'])get(prefix+'-'+field).onchange=()=>{if(prefix==='risk')edit();targetChoices(prefix);};
    if(prefix==='risk')for(const field of ['from','to'])get(prefix+'-'+field).oninput=edit;
  }
  for(const choice of choices())choice.onchange=()=>{edit();showChoice();};
  function renderStress(){
    get('risk-stress-stack').replaceChildren(...stress.map((layer,index)=>{const item=document.createElement('li'),level=document.createElement('span'),label=document.createElement('strong'),amount=document.createElement('span');level.className='risk-layer-number';level.textContent=String(index+1);label.textContent=publicText(layer.label);amount.textContent=`Add ${layer.shift.size} ${layer.unit} to parent`;item.append(level,label,amount);return item;}));
    get('risk-scenario').textContent=stress.length?`Risk around ${stress.length} nested ${stress.length===1?'stress':'stresses'}. Base, up and down values all include this stress stack.`:'Risk around the selected unchanged market.';
    sync();
  }
  /** The setup changed: the run on screen no longer answers it. */
  function edit(){generation++;forgetRun();sync();}
  select.onchange=()=>{edit();targetDetails('risk');};
  bump.oninput=edit;
  stressFactor.onchange=()=>targetDetails('risk-stress');
  // A stress layer is a choice of the person's, made with a button rather than in a field.
  const chose=(event:Event)=>{if(event.isTrusted)edits.mark();};
  get('risk-stress-add').onclick=event=>void options.act(async()=>{
    if(!catalog||!stressFactor.value||!stressSize.value)throw Error('Choose a scenario target and enter a signed absolute shift.');
    const target=catalog.scenarioTargets.find(f=>f.id===stressFactor.value);if(!target)throw Error('Choose an available scenario target.');
    if(stress.length>=16)throw Error('A scenario supports at most sixteen nested stresses.');
    const shift=scenarioShift(target,stressSize.value,catalog.stressConvention,bucket('risk-stress'));
    chose(event);edit();stress=[...stress,{id:target.id,shift,label:target.label+(target.kind==='delivery_bucket'?` · ${shift.factor.target.from} to ${shift.factor.target.to} (exclusive)` :''),unit:target.unit}];stressSize.value='';renderStress();
  });
  get('risk-stress-parent').onclick=event=>{chose(event);edit();stress=stress.slice(0,-1);renderStress();};
  get('risk-stress-clear').onclick=event=>{chose(event);edit();stress=[];renderStress();};
  /** The requests of the chosen report, in parts of at most sixteen shocks, what it was set up as, and the sizes a preset was entered with. */
  function requests():{parts:Request[];setup:RunSetup;sizes?:Record<string,string>}{
    if(!catalog||!scope)throw Error('Open the risk report from the current Blotter first.');
    const base={...scope,pricingPolicy:catalog.pricingPolicy,convention:catalog.convention,numericalPolicy:catalog.numericalPolicy};
    const choice=chosen();
    if(choice===null)throw Error('Choose a report: Shock every held contract, Parallel shift or Custom scenario.');
    if(choice==='custom'){
      const target=catalog.scenarioTargets.find(f=>f.id===select.value);
      return {parts:[structuredClone({...base,bumps:[scenarioShift(target,bump.value,catalog.bumpConvention,bucket('risk'))],stress:stress.map(layer=>layer.shift)})],
        setup:{report:'custom',label:'Custom scenario',sizes:{},withoutTarget:[]}};
    }
    const targets=presetTargets(catalog,choice),withoutTarget=presetGaps(catalog,choice);
    if(!targets.length)throw Error(presetText[choice].none);
    const entered=Object.fromEntries(sizesByUnit(targets).map(unit=>[unit,sizes.get(unit)?.value.trim()??'']));
    const parts=splitBumps(presetRequest(targets,entered,catalog.bumpConvention)).map(part=>structuredClone({...base,bumps:part,stress:[]}));
    return {parts,setup:{report:choice,label:presetText[choice].label,sizes:entered,withoutTarget:[...withoutTarget]},sizes:entered};
  }

  // --- running and queueing --------------------------------------------------------------------------------------------------
  /** Runs the parts one after another. A part that fails is said to have failed, and the others still run; a single part that fails leaves no run. */
  async function runNow(current:Run,version:number){
    let first:unknown;const stale=()=>version!==generation;
    await runParts(current.parts,stale,async(part,index)=>{
      if(current.parts.length>1)get('risk-progress').textContent=`Running part ${index+1} of ${current.parts.length}…`;
      try{part.report=await options.api<Report>('portfolio/risk',part.request);}
      catch(error){if(stale())return false;denied(error);if(stale())throw error;part.failure=failure(error,'The part could not be run.');first??=error;}
      return true;
    });
    if(stale())return;
    get('risk-progress').textContent='';
    if(current.parts.length===1&&first!==undefined)forgetRun();else showRun();
    if(first!==undefined)throw first;
  }
  /**
   * Queues each part as its own job, one after another, each kept as the request it was first sent as until its outcome is known. A part
   * whose submission has no known outcome keeps the run retained for the person to reconcile; one refused for good was not queued, and says why.
   * Once the run is no longer the one on screen, no further part is sent.
   */
  async function submit(current:Run,version:number){
    const owner=options.profile();let first:unknown;const stale=()=>version!==generation;
    await runParts(current.parts,stale,async part=>{
      // A part is sent once and then only again with the same identity: a queued or refused part is settled, and one not yet sent is begun.
      if(!part.recovery?.command){
        if(part.job||part.failure!==undefined)return true;
        part.recovery=new PendingCommand(knownRiskRejection);part.recovery.begin('portfolio/risk/jobs',{request:part.request});
      }
      const command=part.recovery.command!,previouslyUncertain=part.recovery.sending();
      try{
        // A request sent again may return its job already done, with its result.
        const job=await options.api<Job>('portfolio/risk/jobs',{requestId:command.operationId,request:command.body.request});
        part.recovery!.clear();take(part,job);
      }catch(error){
        part.recovery!.failed(code(error),previouslyUncertain);
        if(!part.recovery!.command)part.failure=failure(error,'The part was not queued.');
        first??=error;
        if(code(error)==='access_denied')return false;
      }
      return true;
    });
    if(!uncertain(current).length&&submissions.get(owner)===current)submissions.delete(owner);
    if(stale())return;
    if(first!==undefined){denied(first);if(version!==generation)throw first;}
    if(current.parts.length===1&&current.parts[0].failure!==undefined){forgetRun();sync();}else showRun();
    if(first!==undefined){if(code(first)==='risk_job_conflict'&&uncertain(current).length&&await recentJobs(version))return;throw first;}
  }
  async function queue(current:Run,version:number){
    // The run is retained before its first part is sent: a reply that is lost leaves it to be reconciled.
    submissions.set(options.profile(),current);
    await submit(current,version);
  }
  form.onsubmit=event=>{event.preventDefault();const queued=(event as SubmitEvent).submitter?.id==='risk-queue';void options.act(async()=>{
    const plan=requests();forgetRun();const version=++generation;
    // What was run is no longer an edit: the run answers it, and the setup it was carried over in is the run's.
    edits.clear();get('risk-carried').hidden=true;
    const current:Run={queued,parts:plan.parts.map(request=>({request})),context:catalog!.context,asOf:catalog!.asOf,setup:plan.setup};
    run=current;revealResults=true;
    sync();
    try{if(queued){await queue(current,version);if(version===generation&&running())reveal('risk-job');}else await runNow(current,version);}
    // The sizes are prefilled next time once the backend has taken a part of a run of them.
    finally{if(plan.sizes&&current.parts.some(p=>p.report||p.job))storeSizes(plan.sizes);}
  });};

  // --- jobs --------------------------------------------------------------------------------------------------------------------
  /** Reads the recent jobs, and settles a retained part whose original job is among them. Whether that left the run fully known. */
  async function recentJobs(version:number){
    const loaded=await call<Job[]>('portfolio/risk/jobs');if(version!==generation)return false;recent=loaded;
    refresh();
    const retained=submissions.get(options.profile());
    if(!retained||run!==retained)return false;
    let found=false;
    for(const index of uncertain(retained)){
      const part=retained.parts[index],command=part.recovery!.command!,match=recent.find(j=>j.requestId===command.operationId);
      if(!match)continue;
      const receipt=await call<Job>(`portfolio/risk/jobs/${match.id}`);if(version!==generation)return false;
      if(receipt.id!==match.id||receipt.requestId!==command.operationId)throw Error('The retained job receipt does not match the unconfirmed request.');
      part.recovery!.clear();take(part,receipt);found=true;
    }
    if(!found)return false;
    if(!uncertain(retained).length&&submissions.get(options.profile())===retained)submissions.delete(options.profile());
    showRun();return !uncertain(retained).length;
  }
  function schedulePoll(version:number){stopPolling();pollTimer=setTimeout(()=>void refreshJobs(version),750);}
  // Takes what the backend says of a part's job: its state, and its result or why it has none once it is done.
  function take(part:Part,value:Job){
    if(part.job&&value.id!==part.job.id)throw Error('The risk job receipt does not match its part. Refresh to check its retained outcome.');
    part.job=value;part.report=undefined;part.failure=undefined;
    if(value.state==='completed'){if(value.result)part.report=value.result;else part.failure='The completed job result is unavailable. Refresh its retained outcome.';}
    else if(value.state==='failed'||value.state==='cancelled')part.failure=value.error?.message??jobLabels[value.state];
  }
  async function refreshJobs(version=generation,all=false){
    const current=run;
    if(version!==generation||!current)return;
    if(options.busy()){schedulePoll(version);return;}
    try{
      for(const part of current.parts){
        if(!part.job||!all&&!active(part.job))continue;
        const value=await options.api<Job>(`portfolio/risk/jobs/${part.job.id}`);if(version!==generation)return;take(part,value);
      }
    }catch(error){
      if(version!==generation)return;stopPolling();clearResults();denied(error);if(version!==generation)return;
      get('risk-job-status').textContent='Job status is unavailable. Refresh to check its retained outcome.';get('risk-job-refresh').hidden=false;sync();return;
    }
    showRun();
  }
  function contextLabel(context:FinancialContext|undefined,asOf:string|undefined){return riskContextLabel({context,asOf},options.zone());}
  function refresh(){
    const control=get<HTMLSelectElement>('risk-job-history'),selected=control.value,labels=timeLabels(recent.flatMap(j=>j.asOf?[j.asOf]:[]),options.zone());let index=0;
    control.replaceChildren(new Option('Choose a retained job…',''),...recent.map(j=>new Option(`${j.asOf?labels[index++]:'Unavailable market time'} · ${j.state.replaceAll('_',' ')}`,j.id)));control.value=selected;
    if(run?.queued)get('risk-job-context').textContent=contextLabel(run.context,run.asOf);
  }
  /** Says where each queued part stands, and shows the run's results once none of its parts is still under way. */
  function showRun(){
    const current=run;
    if(!current)return;
    if(current.queued)describeJobs(current);
    if(running())schedulePoll(generation);else{stopPolling();showResults(current);}
    sync();
  }
  function describeJobs(current:Run){
    const total=current.parts.length,unknown=uncertain(current);
    get('risk-job').hidden=false;get('risk-job-context').textContent=contextLabel(current.context,current.asOf);
    const first=current.parts.find(p=>p.report)?.report;
    get('risk-job-scenario').textContent=first?(first.scenario.isBase?'Retained scenario: original market.':`Retained scenario: ${first.scenario.depth} nested stresses. Exact stress inputs are included in the download.`):'';
    const job=current.parts[0].job;
    get('risk-job-status').textContent=unknown.length?(total===1?'Submission outcome is unknown. Reconcile to retry the same request safely. Inspect recent jobs before discarding this request; it may already have committed.'
        :`The submission of ${partsText(unknown,total)} has no known outcome. Reconcile to send the same requests again safely. Inspect recent jobs before discarding them; they may already have committed.`)
      :total===1&&job?`Risk job: ${jobLabels[job.state]??job.state}. ${job.state==='cancellation_requested'?'Waiting for the worker to stop. ':''}Uses the financial context captured at submission.${job.error?` ${job.error.message}`:''}`
      :`Risk run of ${total} parts, each queued as its own job. Each uses the financial context captured at submission.`;
    get('risk-job-parts').replaceChildren(...(total>1?current.parts.map((part,index)=>{
      const item=document.createElement('li'),state=part.recovery?.command?'Submission outcome unknown':part.job?`${jobLabels[part.job.state]??part.job.state}${part.job.state==='cancellation_requested'?' · waiting for the worker to stop':''}`
        :part.failure!==undefined?'Not queued':'Not yet queued';
      item.textContent=publicText(`Part ${index+1} of ${total} · ${state}${part.failure?` · ${part.failure}`:''}`);
      if(part.failure||part.recovery?.command)item.className='risk-part-attention';
      return item;
    }):[]));
    get('risk-job-cancel').hidden=!current.parts.some(p=>p.job&&['queued','running'].includes(p.job.state));
    get('risk-job-refresh').hidden=unknown.length>0||!current.parts.some(p=>p.job);
    get('risk-job-cancel').textContent=total>1?'Cancel jobs':'Cancel job';get('risk-job-refresh').textContent=total>1?'Refresh jobs':'Refresh job';
    get('risk-job-retry').hidden=get('risk-job-discard').hidden=!unknown.length;
    get('risk-job-return').hidden=!reviewing||unknown.length>0;
    get('risk-scope').textContent=unknown.length?'An unconfirmed risk submission uses its original captured financial context below.'
      :reviewing?'Retained portfolio risk, separated by valuation measure and currency. This job uses its captured financial context below.':currentScopeText;
  }
  get('risk-job-retry').onclick=()=>void options.act(async()=>{const current=submissions.get(options.profile());if(!current)throw Error('There is no retained submission to reconcile.');await submit(current,generation);});
  get('risk-job-discard').onclick=()=>void options.act(async()=>{submissions.delete(options.profile());forgetRun();await open();});
  get('risk-job-refresh').onclick=()=>void options.act(async()=>{if(!run)return;stopPolling();await refreshJobs(++generation,true);});
  get('risk-job-cancel').onclick=()=>void options.act(async()=>{
    const current=run;if(!current)return;stopPolling();const version=++generation;
    for(const part of current.parts)if(part.job&&['queued','running'].includes(part.job.state)){const value=await call<Job>(`portfolio/risk/jobs/${part.job.id}/cancel`,{});if(version!==generation)return;take(part,value);}
    showRun();
  });
  get('risk-job-history').onchange=()=>void options.act(async()=>{
    const id=get<HTMLSelectElement>('risk-job-history').value;if(!id)return;
    forgetRun();const version=++generation;reviewing=true;get<HTMLSelectElement>('risk-job-history').value=id;
    get('risk-scenario').textContent='Viewing a retained job under its captured financial context below. Return to the selected context to calculate another scenario.';
    const value=await call<Job>(`portfolio/risk/jobs/${id}`);if(version!==generation)return;
    const part:Part={};take(part,value);run={queued:true,parts:[part],context:value.context,asOf:value.asOf};showRun();
  });
  get('risk-job-return').onclick=()=>{if(!catalog||submissions.has(options.profile()))void options.act(open);else{edit();renderStress();}};

  // --- results -----------------------------------------------------------------------------------------------------------------
  function showResults(current:Run){
    clearResults();
    const reports=current.parts.flatMap(p=>p.report?[p.report]:[]),total=current.parts.length,host=get('risk-results');
    if(!reports.length&&total===1)return;
    // A result that does not say which positions it leaves out is refused, never read as leaving out none; and every part of a run is made
    // at one close over the same books, so each leaves out the same positions.
    if(reports.some(r=>!Array.isArray(r.excludedPositions))){host.append(alertNote('A result of this run does not say which positions it leaves out, so its results are not shown. Run it again.'));return;}
    const left=reports.map(r=>r.excludedPositions!);
    if(new Set(left.map(list=>JSON.stringify(list))).size>1){host.append(alertNote('The parts of this run leave out different positions, so its results are not shown. Run it again.'));return;}
    // What the run was set up as comes first, so that a run that left prices out can be told from a complete one.
    const setup=note(setupText(current.setup));setup.id='risk-run-setup';host.append(setup);
    const factors=reports.reduce((sum,r)=>sum+r.factors.length,0);let opened=false;
    if(total>1)host.append(note(`${plural(factors,'factor')} in ${total} parts. Each factor's results are its own; nothing is added across factors or parts.`));
    if(left.length&&left[0].length)host.append(note(`Leaves out ${plural(left[0].length,'position')} this close cannot value: ${left[0].map(e=>`${e.tradeReference} (${valuationReason(e.reason,e.instrumentType)})`).join('; ')}.`));
    for(const [partIndex,part] of current.parts.entries()){
      const name=`Part ${partIndex+1} of ${total}`;
      if(part.failure!==undefined){const line=alertNote(`${name} failed: ${part.failure}`);line.classList.add('risk-part-failed');host.append(line);continue;}
      // A part with no outcome once none is under way has a submission with no known outcome, or is not yet queued: the run is
      // retained for Reconcile, which settles the one and queues the other.
      if(!part.report){const line=note(`${name} has no results: ${part.recovery?.command?'its submission has no known outcome':'it is not yet queued'}.`);line.classList.add('risk-part-missing');host.append(line);continue;}
      for(const [factorIndex,item] of part.report.factors.entries()){host.append(factorSection(part.report,item,`${partIndex}-${factorIndex}`,!opened));opened=true;}
    }
    get('risk-work').replaceChildren(...current.parts.flatMap((part,index)=>part.report?[workEvidence(part.report,total>1?`Part ${index+1} of ${total} · `:'')]:[]),
      note('Tables and charts use backend display precision; sorting compares displayed amounts. Hover over a value or download for exact amounts.'));
    get('risk-export').hidden=false;
    get('risk-export-note').hidden=complete(current);get('risk-export-note').textContent=complete(current)?'':'The download needs the results of every part of the run.';
    if(mounted())for(const draw of draws)draw();
    if(revealResults&&mounted()){revealResults=false;reveal('risk-results');}
  }
  function note(text:string){const p=document.createElement('p');p.className='field-help';p.textContent=publicText(text);return p;}
  function alertNote(text:string){const line=note(text);line.setAttribute('role','alert');return line;}
  /** One factor's book totals, chart and holding results, drawn when its section is open in the panel: the first is open. */
  function factorSection(report:Report,item:Report['factors'][number],key:string,open:boolean){
    const section=document.createElement('details'),summary=document.createElement('summary'),title=document.createElement('strong'),size=document.createElement('span');
    section.className='risk-factor';section.open=open;title.textContent=factorLabel(item.factor);size.textContent=publicText(`±${item.bump} · ${item.factor.unit}`);summary.append(title,' ',size);section.append(summary);
    let drawn=false;
    const draw=()=>{
      if(drawn||!section.open||!section.isConnected||!mounted())return;drawn=true;
      for(const [partitionIndex,totals] of riskTotalGroups(item.totals).entries()){
        const first=totals[0],unit=first.values;
        const heading=document.createElement('h4');heading.textContent=publicText(`${first.measureLabel} · ${factorLabel(item.factor)} · absolute bump ±${item.bump} · unit ${item.factor.unit}`);
        const host=document.createElement('div');host.className='table-wrap';section.append(heading,host);
        const headers=['Book',`Base ${first.measureLabel} (${unit.currency})`,`Up ${first.measureLabel} (${unit.currency})`,`Down ${first.measureLabel} (${unit.currency})`,`Up change (${unit.currency})`,`Down change (${unit.currency})`,`Delta (${unit.deltaUnit})`,`Gamma (${unit.gammaUnit})`];
        const amounts=(value:RiskValues['display'])=>[value.base,value.up,value.down,value.upPnl,value.downPnl,value.delta,value.gamma];
        grids.push(renderDataGrid(host,`risk-totals-${key}-${partitionIndex}`,report.snapshotId+report.context.contextId,headers,totals.map(t=>[t.book,...amounts(t.values.display)]),[1,2,3,4,5,6,7],options.busy,
          {keys:['book','base','up','down','upPnl','downPnl','delta','gamma'],cellTitles:totals.map(t=>[t.book,...amounts(t.values)]),layout:{order:[],widths:{},sort:[]},onLayout:()=>{}}));
        const plot=document.createElement('div');plot.className='chart';section.append(plot);charts.push(plot);
        renderChart(plot,{title:`${first.measureLabel} change by book`,subtitle:`±${item.bump} price points · ${item.factor.unit} · displayed amounts`,axis:'category',unit:unit.currency,kind:'bar',series:[
          {name:'Up shock',points:totals.map(t=>({x:t.bookId,label:t.book,y:t.values.display.upPnl}))},
          {name:'Down shock',points:totals.map(t=>({x:t.bookId,label:t.book,y:t.values.display.downPnl}))},
        ]});
      }
      const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Individual holding results';details.append(summary);
      const rows=document.createElement('div');rows.className='table-wrap';details.append(rows);section.append(details);
      // Initialize after opening: the virtual grid needs a visible width.
      let ready=false;details.ontoggle=()=>{if(!details.isConnected||!details.open||ready)return;ready=true;const keys=['book','trade','instrument','instrumentType','measure','currency','base','upPnl','downPnl','delta','deltaUnit','gamma','gammaUnit'];
        const cells=(p:Position,display:boolean)=>{const v=display?p.values.display:p.values;return [p.book,p.tradeReference,p.instrumentReference,p.instrumentType,p.measureLabel,p.values.currency,v.base,v.upPnl,v.downPnl,v.delta,p.values.deltaUnit,v.gamma,p.values.gammaUnit];};
        grids.push(renderDataGrid(rows,`risk-positions-${key}`,report.snapshotId,['Book','Trade','Instrument','Instrument type','Measure','Currency','Base value','Up change','Down change','Delta','Delta unit','Gamma','Gamma unit'],item.positions.map(p=>cells(p,true)),[6,7,8,9,11],options.busy,{keys,cellTitles:item.positions.map(p=>cells(p,false)),layout:{order:[],widths:{},sort:[]},onLayout:()=>{}}));};
    };
    section.ontoggle=draw;draws.push(draw);
    return section;
  }
  function workEvidence(report:Report,prefix:string){
    const w=report.work,wrap=document.createElement('div'),overview=document.createElement('p');
    overview.textContent=publicText(`${prefix}${w.positionCount} holdings · ${report.heldInstruments} held instruments · ${w.uniqueValuations} unit valuations · ${w.scenarioCount} valuation scenarios · ${w.preparationScenarioCount} preparation scenarios · ${w.unitPricerCalls} pricer calls · ${w.repeatedInstrumentReferences} additional quantity references · ${w.quantityApplications} quantity applications.`);
    const evidence=document.createElement('details'),title=document.createElement('summary'),rows=document.createElement('ul');title.textContent='Graph and preparation work';
    for(const line of riskWorkDetails(report)){const item=document.createElement('li');item.textContent=line;rows.append(item);}
    evidence.append(title,rows);wrap.append(overview,evidence);return wrap;
  }
  get('risk-export').onclick=()=>void options.act(async()=>{
    const current=run,version=generation;
    if(!current||!complete(current))throw Error('The download needs every part of the run. Run it again.');
    // Each part is read again under current authority, as its own request or its own retained job.
    const parts:Report[]=[];
    for(const part of current.parts){
      const report=part.job?(await call<Job>(`portfolio/risk/jobs/${part.job.id}`)).result:await call<Report>('portfolio/risk',part.request);
      if(version!==generation)return;
      if(!report)throw Error('The completed job result is unavailable. Refresh its retained outcome.');
      parts.push(report);
    }
    const blob=new Blob([JSON.stringify({setup:current.setup??null,parts},null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download='leanetrm-risk.json';link.click();URL.revokeObjectURL(url);
  });

  function sync(){
    describeStatus();
    const retained=uncertain(run).length>0,locked=options.busy()||running()||retained||reviewing||reading||!catalog;
    form.hidden=reviewing||retained;get<HTMLFieldSetElement>('risk-fields').disabled=locked;
    const runnable=options.canRun(),reason=get('risk-run-note');
    reason.hidden=runnable;reason.textContent=runnable?'':riskDenied;
    for(const id of ['risk-run','risk-queue']){const button=get<HTMLButtonElement>(id);button.disabled=locked||!runnable;if(runnable)button.removeAttribute('aria-describedby');else button.setAttribute('aria-describedby',reason.id);}
    get<HTMLButtonElement>('risk-export').disabled=options.busy()||!complete(run);get('risk-results').inert=options.busy();
    get<HTMLButtonElement>('risk-stress-parent').disabled=locked||!stress.length;get<HTMLButtonElement>('risk-stress-clear').disabled=locked||!stress.length;get<HTMLButtonElement>('risk-stress-add').disabled=locked||stress.length>=16;
    get<HTMLSelectElement>('risk-job-history').disabled=options.busy();get<HTMLButtonElement>('risk-reload').disabled=options.busy();
    for(const id of ['risk-job-refresh','risk-job-retry','risk-job-cancel','risk-job-return','risk-job-discard'])get<HTMLButtonElement>(id).disabled=options.busy();
    options.onChange();
  }
  get('risk-reload').onclick=()=>void options.act(open);

  return {
    /** Opens the report for the Blotter on screen: the setup and run it holds, or a retained submission, or a new catalog read. */
    open,reset,sync,refresh,
    /** Puts the report in the panel's Risk report mode, with its head. */
    mount(shell:Pick<SidePanel,'element'>){
      const title=document.createElement('h2'),sub=document.createElement('p');
      title.id='side-panel-title';title.className='side-panel-title';title.textContent='Risk report';
      sub.className='side-panel-sub';sub.textContent='The books on screen under shared market shocks, run now or queued as jobs.';
      shell.element('head').replaceChildren(title,sub);
      const body=shell.element('body');body.setAttribute('role','group');body.setAttribute('aria-label','Risk report');
      shell.element('view').replaceChildren(root);root.hidden=false;
      for(const draw of draws)draw();
      sync();
    },
    /** Takes the report out of the panel; what it holds stays for when it is opened again. */
    shelve(){if(root.parentElement!==shelf)shelf.append(root);root.hidden=true;},
    /** Gives up the setup's edits, which the person has agreed to leave: the report starts afresh. Without edits it is left as it is. */
    dropEdits(){if(edited())reset();},
    /**
     * The inputs on screen changed, by a live correction or a settlement record: what the report holds is forgotten, as `reset` does, so
     * that a run is only ever made at the inputs on screen. With `carry` the setup's choices are kept for the next catalog read, which
     * puts them back and names what it no longer offers.
     */
    inputsChanged(carrying:boolean){const kept=carrying?carried??choicesNow():undefined;reset();carry(kept);},
    edited,
    unconfirmed:()=>submissions.has(options.profile()),
    /** Whether results of a run are on screen, which a later change of the inputs they were run on clears. */
    shown:()=>!!run&&run.parts.some(p=>p.report),
    /** Whether a queued part of the run on screen is still under way. */
    running,
  };
}
