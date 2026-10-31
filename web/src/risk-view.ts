import {projectionPolicyLabels} from './projection-policy.js';
// The risk report of the Blotter desk (blotter-desk.md, Side panel: Risk report), drawn in the side panel's Risk report mode. It offers
// Shock every held contract and Parallel shift, which shock the targets the catalog names for what the books hold, and the custom
// scenario lab with nested stresses; it runs a report now or queues it as jobs and keeps the retained job history. A run's results come
// back inline in the desk (D222), which reads the run on screen from `current`. A run of more than sixteen shocks is split into parts of
// at most sixteen at the one retained context of its catalog, run one after another or queued as one job each, which states the run it
// is a part of, and shown as one run. Each factor's results are its own, so no arithmetic combines parts, and a part that fails is said to
// have failed. What the report holds outlives the panel's other modes; a new read of the Blotter clears it.
import {riskContextLabel} from './risk-time.js';
import {riskWorkDetails,type ExcludedPosition,type RiskReport} from './risk-report.js';
import {PendingCommand} from './pending-command.js';
import {publicText,timeLabels} from './market-view.js';
import {scenarioShift,knownRiskRejection,type ScenarioTarget,type ScenarioShift} from './risk-scenarios.js';
import {presetGaps,presetRequest,presetTargets,runParts,sizesByUnit,splitBumps,type Preset} from './risk-presets.js';
import {valuationReason} from './valuation-status.js';
import {trackEdits} from './edit-tracking.js';
import type {SidePanel} from './side-panel.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type FinancialContext={contextId:string;effectiveOn:string;knownAt?:string;knownThrough:number};
type Catalog={pricingPolicy:unknown;scenarioTargets:ScenarioTarget[];heldContractTargets:string[];heldCurveTargets:string[];
  heldContractsWithoutTarget:string[];heldCurvesWithoutTarget:string[];excludedPositions:ExcludedPosition[];bumpConvention:string;stressConvention:string;
  convention:string;numericalPolicy:string;context:FinancialContext;asOf:string};
type Scope={context:unknown;bookIds:string[];portfolioIds:string[]};
type StressLayer={id:string;shift:ScenarioShift;label:string;unit:string};
type Request=Scope&{pricingPolicy:unknown;bumps:ScenarioShift[];stress:ScenarioShift[];convention:string;numericalPolicy:string};
export type JobState='queued'|'running'|'completed'|'failed'|'cancellation_requested'|'cancelled';
/** The run a queued part belongs to (D222): the run's identity, which part this is and how many parts the run has. */
type JobRun={id:string;part:number;parts:number};
/** The books and portfolios a run was asked for, as they were chosen. */
type RunScope={bookIds:readonly string[];portfolioIds:readonly string[]};
type Job={id:string;requestId?:string;state:JobState;error?:{code:string;message:string};result?:RiskReport;context?:FinancialContext;asOf?:string;
  finishedAt?:string|null;run:JobRun|null;scope?:RunScope|null;shocks?:number};
/**
 * A part of a run and what became of it: the report it returned, or the job it was queued as, or why it failed. A queued part's
 * submission is kept as its `recovery` until its outcome is known. A retained job read from the history has no request of its own, and a
 * part of a retained run that the history does not list is `unlisted`.
 */
type Part={request?:Request;report?:RiskReport;job?:Job;failure?:string;recovery?:PendingCommand;unlisted?:boolean};
type Choice=Preset|'custom';
/**
 * What a run was set up as: its report, its shock size for each price unit, and the held prices or curves the preset did not shock,
 * as no whole target covers them. A custom scenario's sizes are its factors' own.
 */
type RunSetup={report:Choice;label:string;sizes:Record<string,string>;withoutTarget:string[]};
/**
 * A run on screen: run now or queued, its parts in the order of its shocks, the context it was made at, and its setup and the choices it
 * was made from, which a retained job read from the history does not record. `key` tells one run on screen from another; `id` is the
 * identity a run queued in parts states with each part; `scope` the books and portfolios it was asked for, which a job read from the
 * history states too; `making` is a run made now that is under way, and `finishedAt` when a run made now finished.
 */
type Run={key:number;queued:boolean;parts:Part[];context?:FinancialContext;asOf?:string;setup?:RunSetup;choices?:Choices;id?:string;scope?:RunScope;making?:boolean;finishedAt?:string};
/** A part of the run on screen as the desk shows it: its report once it has one, or what became of it. */
export type RunPart={report?:RiskReport;job?:{state:JobState;finishedAt?:string|null;shocks?:number};failure?:string;unknown:boolean;unlisted:boolean};
/**
 * The run on screen as the desk shows it (D222): what it was set up as, in words; whether it was queued, whether a queued part of it is
 * still under way, and whether it is retained, a job of the history or an unconfirmed submission shown under the context it was captured
 * at; where and when it was made, and for which books and portfolios, when that is known; how many shocks it was asked for, when that is
 * known; and its parts.
 */
export type RunView={key:number;queued:boolean;running:boolean;retained:boolean;setup?:RunSetup;setupText:string;context?:FinancialContext;asOf?:string;finishedAt?:string;
  scope?:RunScope;factors:number|null;parts:readonly RunPart[]};
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
/** The most parts a queued run takes, as the job store admits them: sixteen shocks to a part, 4,096 in all. */
const queuedParts=256;
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
  if(!setup)return 'A retained run: its report and sizes are not recorded with it, and each shock states its size.';
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
  let generation=0,pollTimer:ReturnType<typeof setTimeout>|undefined,run:Run|undefined,reviewing=false,recent:Job[]=[],runs=0;
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
  function clearWork(){get('risk-work').replaceChildren();}
  function clearJob(){
    get('risk-job').hidden=true;for(const id of ['risk-job-context','risk-job-scenario','risk-job-status'])get(id).textContent='';get('risk-job-parts').replaceChildren();
    get('risk-job-return').hidden=true;get<HTMLSelectElement>('risk-job-history').value='';get('risk-scope').textContent=currentScopeText;
  }
  /** Forgets the run on screen, its results and its jobs: the setup stays. */
  function forgetRun(){stopPolling();run=undefined;reviewing=false;clearWork();clearJob();get('risk-progress').textContent='';}
  function reveal(id:string){if(mounted())get(id).scrollIntoView({block:'start'});}
  function resetControls(){
    select.replaceChildren(new Option('Choose a scenario target…',''));stressFactor.replaceChildren(new Option('Choose a scenario target…',''));
    bump.value='';stressSize.value='';get('risk-unit').textContent='';get('risk-stress-unit').textContent='';
    get<HTMLDetailsElement>('risk-stress').open=false;
    for(const prefix of ['risk','risk-stress']){get<HTMLSelectElement>(prefix+'-coordinate').value='all';get<HTMLSelectElement>(prefix+'-target-kind').value='all';get(prefix+'-bucket').hidden=true;
      for(const suffix of ['from','to']){const input=get<HTMLInputElement>(prefix+'-'+suffix);input.value='';input.required=false;input.disabled=true;}}
    for(const choice of choices())choice.checked=false;
  }
  /** Forgets everything the report holds but the submissions whose outcome is unknown, which are kept until they are settled. */
  function reset(){
    generation++;forgetRun();catalog=undefined;scope=undefined;stress=[];recent=[];reading=false;unread=undefined;sizes=new Map();carried=undefined;
    get('risk-carried').hidden=true;get('risk-carried').textContent='';get('risk-job-history').replaceChildren(new Option('Choose a retained job…',''));
    resetControls();edits.clear();renderStress();showChoice();describe();sync();
  }
  /**
   * Starts the setup again, as the person agreed to give its edits up: on the choices the run on the desk was made from, which Edit shocks
   * opens it on, or afresh. The run is the desk's: only a change of close, context or books, a new read, a live correction or a settlement
   * record clears it, each with its note (D222).
   */
  function restartSetup(){
    stress=[];carried=undefined;get('risk-carried').hidden=true;get('risk-carried').textContent='';
    resetControls();offer();if(run?.choices&&catalog)put(run.choices);
    edits.clear();renderStress();showChoice();describe();sync();
  }
  function denied(error:unknown){if(code(error)==='access_denied'){reset();options.onDenied();}}
  async function call<T>(path:string,body?:unknown):Promise<T>{try{return await options.api<T>(path,body);}catch(error){denied(error);throw error;}}
  const factorLabel=(f:ScenarioTarget)=>publicText(f.label);

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
    // A catalog read for the Blotter on screen stands with the setup and run it holds: any new read of the Blotter resets the report. The
    // history is read again, for what was queued or has ended since.
    if(catalog){sync();await readHistory(generation);return;}
    // A new read keeps the choices carried over from inputs that changed, and puts them back over the catalog it reads.
    const kept=carried;reset();carry(kept);const version=generation;reading=true;describe();sync();
    try{
      scope=structuredClone(options.scope());
      const loaded=await call<Catalog>('portfolio/risk/catalog',scope);if(version!==generation)return;
      // A catalog that does not say which positions a run leaves out is refused, never read as saying none.
      if(!Array.isArray(loaded.excludedPositions))throw Error('The risk catalog is inconsistent. Reload it.');
      catalog=loaded;
      scope={...scope,context:{...(scope.context as Record<string,unknown>),knowledgeMode:'retained',contextId:catalog.context.contextId}};
      offer();
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
  /** Offers the setup what the catalog read for the Blotter on screen holds: its scenario targets, and the sizes last used, prefilled. */
  function offer(){
    sizes=new Map([...storedSizes()].map(([unit,value])=>[unit,{value,prefilled:true}]));
    for(const prefix of ['risk','risk-stress'])targetChoices(prefix);
  }
  /**
   * Puts choices back in the setup over the catalog it has, and says what the catalog no longer offers of them: a chosen scenario target
   * or stress, or a size typed for a price unit no target of the chosen preset is quoted in.
   */
  function put(kept:Choices){
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
    return gone;
  }
  /** Puts the person's choices back over a catalog read at changed inputs, as their edits still, and names what it no longer offers. */
  function restore(kept:Choices){
    const gone=put(kept),line=get('risk-carried');line.hidden=false;
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
      try{const report=await options.api<RiskReport>('portfolio/risk',part.request);projectionPolicyLabels(report.projectionPolicies);part.report=report;}
      catch(error){if(stale())return false;denied(error);if(stale())throw error;part.failure=failure(error,'The part could not be run.');first??=error;}
      return true;
    });
    if(stale())return;
    // The time the run finished is this page's clock: the run bar says when it ran.
    current.making=false;current.finishedAt=new Date().toISOString();
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
    await runParts(current.parts,stale,async(part,index)=>{
      // A part is sent once and then only again with the same identity: a queued or refused part is settled, and one not yet sent is begun.
      // A part of a run made in parts states the run, its part and the run's part count (D222).
      if(!part.recovery?.command){
        if(part.job||part.failure!==undefined)return true;
        const run:JobRun|undefined=current.id===undefined?undefined:{id:current.id,part:index+1,parts:current.parts.length};
        part.recovery=new PendingCommand(knownRiskRejection);part.recovery.begin('portfolio/risk/jobs',{request:part.request,...(run?{run}:{})});
      }
      const command=part.recovery.command!,previouslyUncertain=part.recovery.sending();
      try{
        // A request sent again may return its job already done, with its result.
        const job=await options.api<Job>('portfolio/risk/jobs',{requestId:command.operationId,request:command.body.request,...(command.body.run?{run:command.body.run}:{})});
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
    // The history lists what was queued: a run queued in parts as one run. A part whose submission has no known outcome is left to Reconcile.
    if(version===generation)await readHistory(version);
  }
  form.onsubmit=event=>{event.preventDefault();const queued=(event as SubmitEvent).submitter?.id==='risk-queue';void options.act(async()=>{
    const plan=requests();
    if(queued&&plan.parts.length>queuedParts)throw Error(`A queued run takes at most ${queuedParts} parts of sixteen shocks. Choose fewer, or run it now.`);
    forgetRun();const version=++generation;
    // What was run is no longer an edit: the run answers it, and the setup it was carried over in is the run's.
    edits.clear();get('risk-carried').hidden=true;
    const current:Run={key:++runs,queued,making:!queued,parts:plan.parts.map(request=>({request})),context:catalog!.context,asOf:catalog!.asOf,setup:plan.setup,choices:choicesNow(),
      scope:{bookIds:[...scope!.bookIds],portfolioIds:[...scope!.portfolioIds]},
      ...(queued&&plan.parts.length>1?{id:crypto.randomUUID()}:{})};
    run=current;
    sync();
    try{if(queued){await queue(current,version);if(version===generation&&running())reveal('risk-job');}else await runNow(current,version);}
    // The sizes are prefilled next time once the backend has taken a part of a run of them.
    finally{if(plan.sizes&&current.parts.some(p=>p.report||p.job))storeSizes(plan.sizes);}
  });};

  // --- jobs --------------------------------------------------------------------------------------------------------------------
  /** Reads the recent jobs for the history. Whether the read is still the report's. */
  async function readHistory(version:number){
    const loaded=await call<Job[]>('portfolio/risk/jobs');if(version!==generation)return false;
    recent=loaded;refresh();return true;
  }
  /** Reads the recent jobs, and settles a retained part whose original job is among them. Whether that left the run fully known. */
  async function recentJobs(version:number){
    if(!await readHistory(version))return false;
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
    if(value.state==='completed'){
      if(value.result){try{projectionPolicyLabels(value.result.projectionPolicies);part.report=value.result;}catch(error){part.failure=failure(error,'The completed job result is invalid. Run risk again.');}}
      else part.failure='The completed job result is unavailable. Refresh its retained outcome.';
    }
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
      // A run whose parts have all ended is listed again with how they ended.
      if(!current.parts.some(p=>active(p.job))&&!await readHistory(version))return;
    }catch(error){
      if(version!==generation)return;stopPolling();denied(error);if(version!==generation)return;
      get('risk-job-status').textContent='Job status is unavailable. Refresh to check its retained outcome.';get('risk-job-refresh').hidden=false;sync();return;
    }
    showRun();
  }
  function contextLabel(context:FinancialContext|undefined,asOf:string|undefined){return riskContextLabel({context,asOf},options.zone());}
  /**
   * The retained jobs as the history lists them, newest first: the parts of a run made in parts are one entry (D222), whose state says how
   * its parts stand, and a job queued on its own is another. A run whose parts disagree on their part count is refused, never guessed at.
   */
  function history():{value:string;asOf?:string;text:string}[]{
    const entries:{value:string;asOf?:string;text:string}[]=[],runsSeen=new Map<string,Job[]>();
    for(const job of recent){
      if(job.run===undefined)throw Error('The risk job history is inconsistent. Reload it.');
      if(job.run===null){entries.push({value:job.id,asOf:job.asOf,text:job.state.replaceAll('_',' ')});continue;}
      const listed=runsSeen.get(job.run.id);
      if(listed){listed.push(job);continue;}
      const parts=[job];runsSeen.set(job.run.id,parts);entries.push({value:'run:'+job.run.id,asOf:job.asOf,text:''});
    }
    for(const entry of entries){
      const parts=entry.value.startsWith('run:')?runsSeen.get(entry.value.slice(4))!:undefined;
      if(!parts)continue;
      const count=parts[0].run!.parts;
      if(parts.some(j=>j.run!.parts!==count))throw Error('The risk job history is inconsistent. Reload it.');
      const states=new Map<string,number>();for(const job of parts)states.set(job.state.replaceAll('_',' '),(states.get(job.state.replaceAll('_',' '))??0)+1);
      entry.text=`run of ${count} parts · ${states.size===1&&parts.length===count?[...states.keys()][0]:[...states].map(([state,n])=>`${n} ${state}`).join(', ')}${parts.length<count?` · ${count-parts.length} not among the recent jobs`:''}`;
    }
    return entries;
  }
  function refresh(){
    const control=get<HTMLSelectElement>('risk-job-history'),selected=control.value,entries=history(),labels=timeLabels(entries.flatMap(e=>e.asOf?[e.asOf]:[]),options.zone());let index=0;
    control.replaceChildren(new Option('Choose a retained job…',''),...entries.map(e=>new Option(`${e.asOf?labels[index++]:'Unavailable market time'} · ${e.text}`,e.value)));control.value=selected;
    if(run?.queued)get('risk-job-context').textContent=contextLabel(run.context,run.asOf);
  }
  /** Says where each queued part stands, and shows the run's results once none of its parts is still under way. */
  function showRun(){
    const current=run;
    if(!current)return;
    if(current.queued)describeJobs(current);
    if(running())schedulePoll(generation);else{stopPolling();showWork(current);}
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
        :part.failure!==undefined?'Not queued':part.unlisted?'Missing from the job history':'Not yet queued';
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
    const value=get<HTMLSelectElement>('risk-job-history').value;if(!value)return;
    forgetRun();const version=++generation;reviewing=true;get<HTMLSelectElement>('risk-job-history').value=value;
    get('risk-scenario').textContent='Viewing a retained job under its captured financial context below. Return to the selected context to calculate another scenario.';
    // A run made in parts is read part by part, in the order of its parts; a part the history no longer lists is said to be missing.
    const runId=value.startsWith('run:')?value.slice(4):undefined,listed=runId===undefined?[]:recent.filter(j=>j.run?.id===runId);
    const ids=runId===undefined?[value]:listed.map(j=>j.id),read=new Map<string,Job>();
    for(const id of ids){const job=await call<Job>(`portfolio/risk/jobs/${id}`);if(version!==generation)return;read.set(id,job);}
    const count=runId===undefined?1:listed[0]?.run?.parts;
    if(count===undefined)throw Error('That run is no longer among the recent jobs. Refresh the history.');
    const parts=Array.from({length:count},(_,index):Part=>{
      const job=runId===undefined?read.get(value):[...read.values()].find(j=>j.run?.id===runId&&j.run.part===index+1);
      if(!job)return {unlisted:true};
      const part:Part={};take(part,job);return part;
    });
    // The books and portfolios the run was asked for, which every part states alike, or none when a part does not say.
    const jobs=[...read.values()],first=jobs[0],stated=first.scope??undefined;
    const runScope=stated&&jobs.every(j=>JSON.stringify(j.scope)===JSON.stringify(stated))?stated:undefined;
    run={key:++runs,queued:true,parts,context:first.context,asOf:first.asOf,...(runScope?{scope:runScope}:{}),...(runId===undefined?{}:{id:runId})};showRun();
  });
  get('risk-job-return').onclick=()=>{if(!catalog||submissions.has(options.profile()))void options.act(open);else{edit();renderStress();}};

  // --- the run's work and its download -------------------------------------------------------------------------------------------
  /** The work each part's calculation did, once none of its parts is under way: the results of the run are the desk's (D222). */
  function showWork(current:Run){
    const total=current.parts.length;
    get('risk-work').replaceChildren(...current.parts.flatMap((part,index)=>part.report?[workEvidence(part.report,total>1?`Part ${index+1} of ${total} · `:'')]:[]));
  }
  function workEvidence(report:RiskReport,prefix:string){
    const w=report.work,wrap=document.createElement('div'),overview=document.createElement('p');
    overview.textContent=publicText(`${prefix}${w.positionCount} holdings · ${report.heldInstruments} held instruments · ${w.uniqueValuations} unit valuations · ${w.scenarioCount} valuation scenarios · ${w.preparationScenarioCount} preparation scenarios · ${w.unitPricerCalls} pricer calls · ${w.repeatedInstrumentReferences} additional quantity references · ${w.quantityApplications} quantity applications.`);
    const evidence=document.createElement('details'),title=document.createElement('summary'),rows=document.createElement('ul');title.textContent='Graph and preparation work';
    for(const line of riskWorkDetails(report)){const item=document.createElement('li');item.textContent=line;rows.append(item);}
    evidence.append(title,rows);wrap.append(overview);for(const label of projectionPolicyLabels(report.projectionPolicies)){const note=document.createElement('p');note.textContent=label;wrap.append(note);}wrap.append(evidence);return wrap;
  }
  /** Downloads the run's results and inputs: each part is read again under current authority, as its own request or its own retained job. */
  async function download(){
    const current=run,version=generation;
    if(!current||!complete(current))throw Error('The download needs every part of the run. Run it again.');
    const parts:RiskReport[]=[];
    for(const part of current.parts){
      const report=part.job?(await call<Job>(`portfolio/risk/jobs/${part.job.id}`)).result:await call<RiskReport>('portfolio/risk',part.request);
      if(version!==generation)return;
      if(!report)throw Error('The completed job result is unavailable. Refresh its retained outcome.');
      projectionPolicyLabels(report.projectionPolicies);
      parts.push(report);
    }
    const blob=new Blob([JSON.stringify({setup:current.setup??null,parts},null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download='leanetrm-risk.json';link.click();URL.revokeObjectURL(url);
  }
  /** The run on screen as the desk shows it, once one is: a run made now is shown when its parts have ended. */
  function current():RunView|undefined{
    if(!run||run.making)return undefined;
    const requested=run.parts.every(p=>p.request)?run.parts.reduce((count,p)=>count+p.request!.bumps.length,0):null;
    return {key:run.key,queued:run.queued,running:running(),retained:reviewing,setup:run.setup,setupText:setupText(run.setup),context:run.context,asOf:run.asOf,finishedAt:run.finishedAt,scope:run.scope,factors:requested,
      parts:run.parts.map(p=>({report:p.report,job:p.job&&{state:p.job.state,finishedAt:p.job.finishedAt??null,shocks:p.job.shocks},failure:p.failure,unknown:!!p.recovery?.command,unlisted:!!p.unlisted}))};
  }

  function sync(){
    describeStatus();
    const retained=uncertain(run).length>0,locked=options.busy()||running()||retained||reviewing||reading||!catalog;
    form.hidden=reviewing||retained;get<HTMLFieldSetElement>('risk-fields').disabled=locked;
    const runnable=options.canRun(),reason=get('risk-run-note');
    reason.hidden=runnable;reason.textContent=runnable?'':riskDenied;
    for(const id of ['risk-run','risk-queue']){const button=get<HTMLButtonElement>(id);button.disabled=locked||!runnable;if(runnable)button.removeAttribute('aria-describedby');else button.setAttribute('aria-describedby',reason.id);}
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
      sync();
    },
    /** Takes the report out of the panel; what it holds stays for when it is opened again. */
    shelve(){if(root.parentElement!==shelf)shelf.append(root);root.hidden=true;},
    /** Gives up the setup's edits, which the person has agreed to leave, and starts the setup again; the run stays. Without edits, nothing changes. */
    dropEdits(){if(edited())restartSetup();},
    /**
     * The inputs on screen changed, by a live correction or a settlement record: what the report holds is forgotten, as `reset` does, so
     * that a run is only ever made at the inputs on screen. With `carry` the setup's choices are kept for the next catalog read, which
     * puts them back and names what it no longer offers.
     */
    inputsChanged(carrying:boolean){const kept=carrying?carried??choicesNow():undefined;reset();carry(kept);},
    edited,
    unconfirmed:()=>submissions.has(options.profile()),
    /**
     * The account's unconfirmed submission as another window of the Blotter takes it over (blotter-workbench.md, Pop-out Blotter): each part's
     * request, the job it is known as without its result, which that window reads itself, why it failed, and the request an unconfirmed part
     * was sent as, under its identity. Undefined when there is none.
     */
    handOff(){
      const current=submissions.get(options.profile());if(!current)return undefined;
      return {version:1,queued:current.queued,id:current.id??null,context:current.context??null,asOf:current.asOf??null,setup:current.setup??null,scope:current.scope??null,
        parts:current.parts.map(p=>{const {result,...job}=p.job??{} as Job;return {request:p.request??null,job:p.job?job:null,failure:p.failure??null,command:p.recovery?.command??null};})};
    },
    /** Forgets the account's unconfirmed submission, once another window has taken it over. */
    handedOff(){const kept=submissions.get(options.profile());submissions.delete(options.profile());if(kept&&run===kept)forgetRun();sync();},
    /** Takes over an unconfirmed submission another window handed over, which is shown, to reconcile, when the report opens; one it cannot read is refused. */
    adopt(value:unknown){
      const given=value as ReturnType<NonNullable<typeof this.handOff>>&{};
      const text=(x:unknown)=>typeof x==='string'&&x.length>0;
      if(!given||given.version!==1||typeof given.queued!=='boolean'||!Array.isArray(given.parts)||!given.parts.length||given.parts.some(p=>!p||typeof p!=='object'||
        p.command!==null&&(!text(p.command.operationId)||p.command.path!=='portfolio/risk/jobs'||!p.command.body||typeof p.command.body!=='object')||p.job!==null&&!text(p.job.id))||
        !given.parts.some(p=>p.command!==null))throw Error('The unconfirmed risk submission the Blotter’s window handed over could not be read. Check Risk jobs for its outcome.');
      if(submissions.has(options.profile()))throw Error('An unconfirmed risk submission is already kept here, so the one the Blotter’s window held was not taken over. Check Risk jobs for its outcome.');
      const parts:Part[]=given.parts.map(p=>{
        const part:Part={...(p.request?{request:p.request as Request}:{}),...(p.job?{job:p.job as Job}:{}),...(p.failure!==null?{failure:p.failure}:{})};
        if(p.command){part.recovery=new PendingCommand(knownRiskRejection);part.recovery.command={...structuredClone(p.command),uncertain:true};}
        return part;
      });
      submissions.set(options.profile(),{key:++runs,queued:given.queued,parts,...(given.id?{id:given.id}:{}),...(given.context?{context:given.context}:{}),
        ...(given.asOf?{asOf:given.asOf}:{}),...(given.setup?{setup:given.setup}:{}),...(given.scope?{scope:given.scope}:{})});
      sync();
    },
    /** Whether a queued part of the run on screen is still under way. */
    running,
    current,download,
    /** Takes the run off the desk, on the person's word: the setup, and an unconfirmed submission, stay. */
    clearRun(){if(!run)return;generation++;forgetRun();sync();},
  };
}
