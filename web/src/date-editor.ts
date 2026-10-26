import {displayDate,displayInstant,publicText,friendly} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import {zonedInput} from './user-time.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {trackEdits} from './edit-tracking.js';

type Calendar={name:string;from:string;to:string;weekendDays:number[];closedDates:string[]};
type Definition={name:string;symbol:string;firstMonth:string;lastMonth:string;anchor:string;day:number|null;shortMonth:string;adjustment:string;calendar:Calendar|null};
type CalendarRef={catalogId:string;revisionId:string;code:string};
export type LibraryCalendar={reference:CalendarRef;coverageFrom:string;coverageTo:string;weekendDays:string[];closedDates:string[];openDates:string[]};
type SavedDates={definition:Definition;dates:{month:string;unadjusted:string;date:string}[]};
type Version={id:string;version:number;generation:number;state:'draft'|'published';definition:Definition;selection:string;effectiveFrom:string;effectiveTo:string;publicationOrder:number|null;publishedAt:string|null;calendar:CalendarRef|null};
export type Context={effectiveOn:string;knownThrough:number|null;knownAt?:string;contextId?:string};
type Snapshot={context:Context;coverageFrom:string;certifiedThrough:string};
type Receipt={expression:string;date:string;source:{version?:number;name?:string;kind?:string;context?:Context}};
type ContractInput={label:string;start:string;end:string;lastTrading:string;cutoffUtc:string};
type Contract={label:string;start:string;end:string;lastTradingAt:string;startReference:Receipt;endReference:Receipt;lastTradingReference:Receipt;cutoffUtc:string};
type Candidate={code:string;name:string;context:Context;contracts:Contract[];definitionId?:string;version?:number};
type ContractVersion={id:string;version:number;generation:number;state:'draft'|'published';definition:{code:string;name:string;context:Context;contracts:ContractInput[]};publishedAt:string|null};
type CommandReceipt={requestId:string;id:string;version:number;generation:number;state:'draft'|'published'};
type Choice={code:string;name:string};
type MonthlyExample={definition:Definition;selection:string;effectiveFrom:string;effectiveTo:string;contextDate:string;expression:string};
type ContractExampleChoice=Choice&{description:string;requiresSchedule:boolean};
type ContractExample={definition:{code:string;name:string;context:Context;contracts:ContractInput[]};description:string};
type Library={knownThrough:number;versions:Version[];canEdit:boolean;canPublish:boolean;contractDefinitions:ContractVersion[];calendars:LibraryCalendar[];choices:{anchors:Choice[];shortMonths:Choice[];adjustments:Choice[];selections:Choice[]};examples:{monthly:MonthlyExample|null;contracts:ContractExampleChoice[]}};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;

// Presentation of the served reference calendar; weekday names and dates come from the API.
export function calendarSummary(c:LibraryCalendar){
  const plural=(n:number,word:string)=>`${n} ${word}${n===1?'':'s'}`,last=new Date(c.coverageTo+'T00:00:00Z');last.setUTCDate(last.getUTCDate()-1);
  const parts=[c.weekendDays.length?`Closed ${c.weekendDays.join(' and ')}`:'No weekly closures'];
  if(c.closedDates.length)parts.push(plural(c.closedDates.length,'closed date'));
  if(c.openDates.length)parts.push(plural(c.openDates.length,'opened date'));
  parts.push(`covers ${displayDate(c.coverageFrom)} – ${displayDate(last.toISOString().slice(0,10))}`);
  return parts.join(' · ');
}
// A calendar is chosen by its exact reference version, never by code alone.
export const calendarKey=(c:CalendarRef)=>[c.catalogId,c.revisionId,c.code].join('|');
export function visibleVersions<T extends {state:string}>(versions:T[],author:boolean){return author?versions:versions.filter(v=>v.state==='published');}
export function knownContractDateRejection(code:string|undefined):boolean{
  return !!code&&['access_denied','contract_dates','contract_date_conflict','schedule_conflict','schedule_unpublished','schedule_context','temporal_context','request_format','request_too_large','request_token'].includes(code);
}
export function setupDateEditor(run:(work:()=>Promise<void>)=>Promise<void>,api:Api,isBusy:()=>boolean,zone:()=>string,onContextChanged:()=>void=()=>{},identity:()=>string=()=>'',
  /**
   * What changing the context would drop: `holds` says whether the page holds anything that a change of context gives up, and `mayChange`
   * asks the person whether it may be given up, false meaning they kept it.
   */
  guard:{holds:()=>boolean;mayChange:()=>Promise<boolean>}={holds:()=>false,mayChange:async()=>true},
  /** Runs work that completes a change made in these controls, the end-of-day knowledge of a changed effective date, leaving on screen what the change said. */
  complete:(work:()=>Promise<void>)=>Promise<void>=run){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const input=(id:string)=>get<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>(id);
  const value=(id:string)=>input(id).value;
  const set=(id:string,text:string)=>{input(id).value=text;};
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
  const form=get<HTMLFormElement>('dates-form'),curveForm=get<HTMLFormElement>('curve-dates-form');
  const empty=():Library=>({knownThrough:0,versions:[],canEdit:false,canPublish:false,contractDefinitions:[],calendars:[],choices:{anchors:[],shortMonths:[],adjustments:[],selections:[]},examples:{monthly:null,contracts:[]}});
  let library=empty(),selected:Version|undefined,shownDates:SavedDates|undefined,shownCurve:Candidate|undefined;
  let pinnedContext:Context|undefined,exactKnownAt:string|undefined;
  let contractSelected:ContractVersion|undefined,contractDirty=false,generation=0;
  const pending=new PendingCommand(knownContractDateRejection),dateEdits=trackEdits(form);
  const storageKey=()=>`openetrm.contract-dates.request.${identity()}`;
  const fence=()=>({generation,identity:identity()});
  const current=(started:ReturnType<typeof fence>)=>started.generation===generation&&started.identity===identity();
  function storePending(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* Retained in memory when storage is unavailable. */}}
  const knownInput=zonedInput(get<HTMLInputElement>('date-context-view'),zone,api);
  const clearDates=()=>{shownDates=undefined;get('dates-result').hidden=true;};
  const clearCurve=()=>{shownCurve=undefined;get('curve-dates-result').hidden=true;};
  const clearExpression=()=>{get('date-expression-result').hidden=true;};
  for(const event of ['input','change']){
    form.addEventListener(event,clearDates);curveForm.addEventListener(event,()=>{contractDirty=true;clearCurve();contractState();});get('date-expression-form').addEventListener(event,clearExpression);
    get('date-context').addEventListener(event,()=>{pinnedContext=undefined;clearCurve();clearExpression();settle();});
    get('date-context-view').addEventListener(event,()=>{exactKnownAt=undefined;});
  }
  // The controls of the time context, and the values they held when they last settled: what a change that is not agreed to puts back.
  const contextControls=()=>Array.from(get('date-context').querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'));
  let settled=new Map<Element,string>();
  function settle(){settled=new Map(contextControls().map(control=>[control,control.value]));}
  settle();
  // A change of context clears what the page holds, so while the page holds something that is given up by it, a change the person makes in
  // these controls is held back: none of the page's listeners, or this editor's, hears it until the person has been asked. Agreeing lets it
  // through as if it had just been made, and keeping what the page holds puts the controls back exactly as they were.
  let asking=false,replaying=false;
  const held:{target:EventTarget;type:string}[]=[];
  for(const event of ['input','change'])get('date-context').addEventListener(event,change=>{
    if(replaying||!guard.holds())return;
    change.stopPropagation();
    if(!held.some(h=>h.target===change.target&&h.type===event))held.push({target:change.target!,type:event});
    if(asking)return;
    asking=true;
    void guard.mayChange().then(agreed=>{
      asking=false;
      const changes=held.splice(0);
      if(!agreed){for(const control of contextControls())control.value=settled.get(control)??control.value;return;}
      replaying=true;
      try{for(const {target,type} of changes)target.dispatchEvent(new Event(type,{bubbles:true}));}finally{replaying=false;}
    });
  },true);
  async function sameDayKnowledge(){
    const date=value('date-context-on'),started=fence();
    pinnedContext=undefined;exactKnownAt=undefined;knownInput.clear();
    if(!date)return;
    const snapshot=await api<Snapshot>('date-schedules/context',{effectiveOn:date,knownAt:null,mode:'end_of_day',timeZone:zone()});
    if(!current(started)||date!==value('date-context-on'))return;
    showContext(snapshot.context);
  }
  get('date-context-on').addEventListener('change',()=>void complete(sameDayKnowledge));
  function showContext(context:Context){pinnedContext=context;exactKnownAt=context.knownAt;set('date-context-on',context.effectiveOn);if(context.knownAt)knownInput.set(context.knownAt);else knownInput.clear();settle();}
  function setContext(context:Context){onContextChanged();showContext(context);}
  async function context():Promise<Context>{
    if(!value('date-context-on')||!value('date-context-view'))throw Error('Choose the business date and knowledge time in your display time zone first.');
    if(pinnedContext)return pinnedContext;
    const snapshot=await api<Snapshot>('date-schedules/context',{effectiveOn:value('date-context-on'),knownAt:exactKnownAt??await knownInput.instant(),mode:'at'});
    setContext(snapshot.context);return snapshot.context;
  }
  async function currentKnowledge(){
    if(!value('date-context-on'))throw Error('Choose a context date before capturing current knowledge.');
    const snapshot=await api<Snapshot>('date-schedules/context',{effectiveOn:value('date-context-on'),knownAt:null,mode:'current'});
    setContext(snapshot.context);clearCurve();clearExpression();
  }
  function definition():Definition{
    return {name:value('dates-name'),symbol:value('dates-symbol'),firstMonth:value('dates-first'),lastMonth:value('dates-last'),anchor:value('dates-anchor'),day:value('dates-day')?Number(value('dates-day')):null,shortMonth:value('dates-short'),adjustment:value('dates-adjustment'),calendar:null};
  }
  // The chosen calendar is one exact reference version served by the API; it is never assumed.
  function calendarChoice():LibraryCalendar{
    const found=library.calendars.find(c=>calendarKey(c.reference)===value('dates-calendar'));
    if(!found)throw Error('Choose a business calendar.');return found;
  }
  function calendarNote(){
    const found=library.calendars.find(c=>calendarKey(c.reference)===value('dates-calendar'));
    get('dates-calendar-summary').textContent=found?calendarSummary(found):selected&&!selected.calendar?'This earlier version used a typed calendar. Choose a reference calendar to save a new draft.':'';
  }
  function versionLabel(){
    (input('dates-symbol') as HTMLInputElement).readOnly=!!selected;
    get('dates-editor').hidden=!library.canEdit&&!library.canPublish;
    get('dates-version').textContent=selected?`${selected.definition.symbol} · version ${selected.version} · ${selected.state}${selected.state==='published'?' · saving edits creates a new draft':''}`:'New schedule';
    get('dates-publish').hidden=!library.canPublish||selected?.state!=='draft';get('dates-keep').hidden=!library.canEdit;
  }
  function fill(d:Definition){
    for(const [id,text] of Object.entries({'dates-name':d.name,'dates-symbol':d.symbol,'dates-first':d.firstMonth,'dates-last':d.lastMonth,'dates-anchor':d.anchor,'dates-day':d.day===null?'':String(d.day),'dates-short':d.shortMonth,'dates-adjustment':d.adjustment}))set(id,text);
    clearDates();versionLabel();calendarNote();
  }
  function open(version:Version){
    selected=version;dateEdits.clear();set('dates-calendar',version.calendar?calendarKey(version.calendar):'');fill(version.definition);set('dates-selection',version.selection);set('dates-effective-from',version.effectiveFrom);set('dates-effective-to',version.effectiveTo);versionLabel();
  }
  function showDates(result:SavedDates){
    shownDates=result;get('dates-result-title').textContent=publicText(`${result.definition.symbol} · ${result.dates.length} dates`);
    renderRegularTable(get('dates-table'),'date-occurrences',JSON.stringify(result),['Index','Month','Anchor date','Adjusted date'],result.dates.map((d,i)=>[String(i),d.month.slice(0,7),displayDate(d.unadjusted),displayDate(d.date)]),[],isBusy);get('dates-result').hidden=false;
  }
  function options(id:string,choices:{value:string;name:string}[],placeholder:string){
    const prior=value(id),first=el('option',placeholder);first.value='';get(id).replaceChildren(first,...choices.map(c=>{const o=el('option',c.name);o.value=c.value;return o;}));set(id,choices.some(c=>c.value===prior)?prior:'');
  }
  function list(){
    const author=library.canEdit||library.canPublish,shown=visibleVersions(library.versions,author);
    const host=get('dates-saved');host.replaceChildren();if(!shown.length)host.textContent=author?'No schedules saved yet.':'No schedules have been published yet.';
    for(const version of shown){
      const row=el('div');row.className='shortcut-saved-row';const actions=el('div');actions.className='schedule-actions';
      const view=el('button','View dates');view.type='button';view.className='secondary';view.onclick=()=>void run(async()=>{clearDates();showDates(await previewVersion(version));});actions.append(view);
      if(author){const use=el('button',version.state==='draft'?'Open draft':'Open');use.type='button';use.className='secondary';use.onclick=()=>{if(!isBusy())open(version);};actions.append(use);}
      const label=el('div');label.append(el('strong',`${version.definition.name} · ${version.definition.symbol}`),el('span',`Version ${version.version} · ${version.state} · ${version.calendar?friendly(version.calendar.code)+' calendar':'typed calendar'}`));
      row.append(label,actions);host.append(row);
    }
    const drafts=get('curve-dates-saved');drafts.replaceChildren();if(!library.contractDefinitions.length)drafts.textContent='No shared definitions are available in this view.';
    for(const version of library.contractDefinitions){const row=el('div');row.className='shortcut-saved-row';const actions=el('div');actions.className='schedule-actions';
      const open=el('button','Open');open.type='button';open.className='secondary';open.onclick=()=>{if(!isBusy())openContract(version);};actions.append(open);
      if(version.state==='published'){const resolve=el('button','Resolve in selected context');resolve.type='button';resolve.className='secondary';resolve.dataset.resolve='';resolve.onclick=()=>void run(async()=>{clearCurve();const started=fence();const c=await context();if(!current(started))return;const result=await api<Candidate>('date-schedules/contracts/resolve',{id:version.id,version:version.version,context:c});if(current(started))showCurve(result);});actions.append(resolve);}
      row.append(el('span',`${version.definition.name} · version ${version.version} · ${version.state} · ${version.definition.contracts.length} contracts`),actions);drafts.append(row);
    }
    contractState();
  }
  async function load(){
    const started=fence(),loaded=await api<Library>('date-schedules');if(!current(started))return;library=loaded;
    if(contractSelected&&!library.contractDefinitions.some(v=>v.id===contractSelected?.id&&v.version===contractSelected.version)){contractSelected=undefined;curveForm.reset();get('curve-dates-rows').replaceChildren();contractDirty=false;}
    try{const stored=sessionStorage.getItem(storageKey());pending.command=stored?{...JSON.parse(stored),uncertain:true}:undefined;}catch{pending.clear();}
    // Rule choices are served by the backend that validates them.
    for(const [id,list] of [['dates-anchor',library.choices.anchors],['dates-short',library.choices.shortMonths],['dates-adjustment',library.choices.adjustments],['dates-selection',library.choices.selections]] as const)
      options(id,list.map(c=>({value:c.code,name:c.name})),'Choose…');
    const calendarValue=value('dates-calendar'),placeholder=el('option','Choose a calendar…');placeholder.value='';
    get('dates-calendar').replaceChildren(placeholder,...library.calendars.map(c=>{const o=el('option',friendly(c.reference.code));o.value=calendarKey(c.reference);return o;}));
    set('dates-calendar',library.calendars.some(c=>calendarKey(c.reference)===calendarValue)?calendarValue:'');calendarNote();
    options('curve-dates-schedule',[...new Set(library.versions.filter(v=>v.state==='published').map(v=>v.definition.symbol))].map(symbol=>({value:symbol,name:symbol})),'Choose a published schedule…');
    options('curve-dates-example-kind',library.examples.contracts.map(c=>({value:c.code,name:c.name})),'Choose an example…');
    // The opened form owns its base generation. Refreshing lists must not advance it.
    list();versionLabel();
  }
  // Published typed-calendar (v1) versions are compiled by the retained inline engine.
  const previewVersion=(v:Version)=>v.calendar?api<SavedDates>('date-schedules/preview',{definition:v.definition,calendar:v.calendar}):api<SavedDates>('schedules/dates/preview',v.definition);
  form.onsubmit=e=>{e.preventDefault();void run(async()=>{clearDates();showDates(await api<SavedDates>('date-schedules/preview',{definition:definition(),calendar:calendarChoice().reference}));});};
  get('dates-calendar').addEventListener('change',calendarNote);
  get('dates-keep').onclick=()=>{if(isBusy()||!form.reportValidity())return;void run(async()=>{
    clearDates();const saved=await api<Version>('date-schedules/save',{id:selected?.id??null,version:selected?.version??null,generation:selected?.generation??null,expectedState:selected?.state??null,definition:definition(),calendar:calendarChoice().reference,selection:value('dates-selection'),effectiveFrom:value('dates-effective-from'),effectiveTo:value('dates-effective-to')});
    selected=saved;await load();open(saved);
  });};
  get('dates-publish').onclick=()=>void run(async()=>{
    if(!selected||selected.state!=='draft')throw Error('Open a saved draft first.');
    // Publish the retained draft, never unsaved form edits.
    if(JSON.stringify(definition())!==JSON.stringify(selected.definition)||value('dates-calendar')!==(selected.calendar?calendarKey(selected.calendar):'')||value('dates-selection')!==selected.selection||value('dates-effective-from')!==selected.effectiveFrom||value('dates-effective-to')!==selected.effectiveTo)throw Error('Save your changes before publishing.');
    const published=await api<Version>('date-schedules/publish',{id:selected.id,version:selected.version,generation:selected.generation});selected=published;await load();open(published);
  });
  get('dates-new').onclick=()=>{if(!isBusy()){selected=undefined;form.reset();dateEdits.clear();clearDates();versionLabel();calendarNote();}};
  get('dates-example').onclick=()=>void run(async()=>{selected=undefined;
    const example=library.examples.monthly;if(!example)throw Error('The monthly example is unavailable. Reload schedules.');
    // Exact example terms come from the backend; the calendar stays an explicit choice.
    fill(example.definition);set('dates-selection',example.selection);set('dates-effective-from',example.effectiveFrom);set('dates-effective-to',example.effectiveTo);set('date-context-on',example.contextDate);set('date-expression',example.expression);await currentKnowledge();
  });
  // Capturing current knowledge changes the context, which is asked about first. With no context date it only says so and changes nothing.
  get('date-context-latest').onclick=()=>void (async()=>{if(isBusy()||value('date-context-on')&&!await guard.mayChange())return;await run(async()=>{await load();await currentKnowledge();});})();
  get<HTMLFormElement>('date-expression-form').onsubmit=e=>{e.preventDefault();void run(async()=>{
    clearExpression();const receipt=await api<Receipt>('date-schedules/resolve',{expression:value('date-expression'),context:await context()});
    get('date-expression-result').textContent=publicText(`${receipt.expression} → ${displayDate(receipt.date)}${receipt.source.version?` · ${receipt.source.name} v${receipt.source.version}`:' · literal date'}`);get('date-expression-result').hidden=false;
  });};
  const fields=['label','start','end','lastTrading','cutoffUtc'] as const;
  function addRow(d?:ContractInput){
    if(get('curve-dates-rows').children.length>=120)throw Error('At most 120 contracts in this example.');
    const row=el('tr');for(const field of fields){const td=el('td'),n=el('input');n.required=true;n.value=d?.[field]??'';n.maxLength=field==='label'?80:100;n.setAttribute('aria-label',field);if(field==='cutoffUtc'){n.type='time';n.step='60';}n.dataset.field=field;td.append(n);row.append(td);}
    const td=el('td'),remove=el('button','Remove');remove.type='button';remove.className='text-button';remove.onclick=()=>{if(!isBusy()){row.remove();contractDirty=true;clearCurve();contractState();}};td.append(remove);row.append(td);get('curve-dates-rows').append(row);
  }
  const rows=():ContractInput[]=>Array.from(get('curve-dates-rows').children,row=>Object.fromEntries(Array.from(row.querySelectorAll<HTMLInputElement>('input[data-field]'),n=>[n.dataset.field!,n.value])) as ContractInput);
  async function candidate(){return {code:value('curve-dates-code'),name:value('curve-dates-name'),context:await context(),contracts:rows()};}
  function openContract(version:ContractVersion){contractSelected=version;const d=version.definition;set('curve-dates-code',d.code);set('curve-dates-name',d.name);get('curve-dates-rows').replaceChildren();d.contracts.forEach(addRow);contractDirty=false;clearCurve();clearExpression();contractState();}
  function contractState(){
    const author=library.canEdit;get<HTMLFieldSetElement>('curve-dates-fields').disabled=!author;get('curve-dates-keep').hidden=!author;get('curve-dates-new').hidden=!author;
    get<HTMLInputElement>('curve-dates-code').readOnly=!!contractSelected;
    get('curve-dates-access').hidden=author;get('curve-dates-access').textContent=library.canPublish?'You can publish saved drafts. Editing requires market-definition authoring permission.':'Read-only shared library. Published versions can be resolved in the selected date context.';
    get('curve-dates-version').textContent=contractSelected?`Version ${contractSelected.version} · ${contractSelected.state}${contractDirty?' · unsaved changes':''} · validated ${displayDate(contractSelected.definition.context.effectiveOn)} · known ${contractSelected.definition.context.knownAt?displayInstant(contractSelected.definition.context.knownAt,zone()):'unavailable'}`:'New definition · saving validates and pins schedule versions in the selected context.';
    get('curve-dates-publish').hidden=!library.canPublish||contractSelected?.state!=='draft';get<HTMLButtonElement>('curve-dates-publish').disabled=contractDirty;
    get('curve-dates-pending').hidden=!pending.command;
    get<HTMLButtonElement>('curve-dates-retry').disabled=pending.command?.path.endsWith('/publish')?!library.canPublish:!library.canEdit;
  }
  function showCurve(c:Candidate){
    shownCurve=c;get('curve-dates-result-title').textContent=publicText(`${c.name} · ${c.contracts.length} contracts`);get('curve-dates-spec').textContent=`${c.version?'Saved version '+c.version+' · ':''}Context ${displayDate(c.context.effectiveOn)} · known ${c.context.knownAt?displayInstant(c.context.knownAt,zone()):'from a retained publication prefix'} · end dates excluded`;
    const source=(r:Receipt)=>r.source.version?`v${r.source.version}`:'Literal';
    renderRegularTable(get('curve-dates-table'),'curve-date-contracts',JSON.stringify(c),['Contract','Start reference','Start','Source','End reference','End','Last trading reference',`Last trading · ${zone()}`,'Source'],c.contracts.map(row=>[row.label,row.startReference.expression,displayDate(row.start),source(row.startReference),row.endReference.expression,displayDate(row.end),row.lastTradingReference.expression,displayInstant(row.lastTradingAt,zone()),source(row.lastTradingReference)]),[],isBusy);get('curve-dates-result').hidden=false;
  }
  get('curve-dates-add').onclick=()=>void run(async()=>{addRow();contractDirty=true;clearCurve();contractState();});
  get('curve-dates-example-kind').onchange=()=>{const choice=library.examples.contracts.find(c=>c.code===value('curve-dates-example-kind'));get('curve-dates-example-help').textContent=choice?.description??'Choose an example to review its explicit date conventions.';};
  get('curve-dates-example').onclick=()=>void run(async()=>{
    const choice=library.examples.contracts.find(c=>c.code===value('curve-dates-example-kind'));if(!choice)throw Error('Choose a contract-date example.');
    clearCurve();const example=await api<ContractExample>('date-schedules/contracts/example',{code:choice.code,schedule:choice.requiresSchedule?value('curve-dates-schedule'):null,context:await context()});
    const draft=example.definition;contractSelected=undefined;contractDirty=true;get('curve-dates-rows').replaceChildren();draft.contracts.forEach(addRow);set('curve-dates-name',draft.name);set('curve-dates-code',draft.code);get('curve-dates-example-help').textContent=example.description;contractState();
  });
  curveForm.onsubmit=e=>{e.preventDefault();void run(async()=>{clearCurve();showCurve(await api<Candidate>('date-schedules/contracts/preview',await candidate()));});};
  async function send(command:RequestCommand){
    const started=fence(),uncertain=pending.sending();storePending();contractState();let result:CommandReceipt;
    try{result=await api<CommandReceipt>(command.path,{...command.body,requestId:command.operationId});
      if(result?.requestId!==command.operationId||typeof result.id!=='string'||!/^[-0-9a-f]{36}$/i.test(result.id)||!Number.isInteger(result.version)||result.version<1||!Number.isInteger(result.generation)||result.generation<1||result.state!==(command.path.endsWith('/publish')?'published':'draft')||command.body.id&&result.id!==command.body.id||command.path.endsWith('/publish')&&(result.version!==command.body.version||result.generation!==command.body.generation||result.state!=='published'))throw Error('The response did not confirm the original command. Inspect the library before retrying.');
    }catch(error){if(current(started)&&pending.command===command){pending.failed((error as {code?:string}).code,uncertain);storePending();contractState();}throw error;}
    if(!current(started)||pending.command!==command)return;pending.clear();storePending();get('curve-dates-status').textContent=`${result.state==='published'?'Published':'Saved draft'} · version ${result.version}.`;contractState();
    await load();if(!current(started))return;const fresh=library.contractDefinitions.find(v=>v.id===result.id&&v.version===result.version&&v.generation===result.generation);if(!fresh)throw Error('The command was confirmed, but its exact saved version is unavailable. Reload the library.');openContract(fresh);
  }
  get('curve-dates-keep').onclick=()=>{if(isBusy()||!library.canEdit||!curveForm.reportValidity())return;void run(async()=>{clearCurve();const body={...await candidate(),id:contractSelected?.id??null,version:contractSelected?.version??null,generation:contractSelected?.generation??null,expectedState:contractSelected?.state??null};const command=pending.begin('date-schedules/contracts/save',body);storePending();await send(command);});};
  get('curve-dates-publish').onclick=()=>{if(isBusy()||!library.canPublish||!contractSelected||contractDirty)return;void run(async()=>{const command=pending.begin('date-schedules/contracts/publish',{id:contractSelected!.id,version:contractSelected!.version,generation:contractSelected!.generation});storePending();await send(command);});};
  get('curve-dates-retry').onclick=()=>{if(!isBusy()&&pending.command)void run(()=>send(pending.command!));};
  get('curve-dates-discard').onclick=()=>{if(isBusy())return;pending.clear();storePending();contractState();};
  get('curve-dates-new').onclick=()=>{if(isBusy()||!library.canEdit)return;contractSelected=undefined;contractDirty=false;curveForm.reset();get('curve-dates-rows').replaceChildren();clearCurve();contractState();};
  get('curve-dates-reload').onclick=()=>void run(async()=>{const id=contractSelected?.id,version=contractSelected?.version;await load();const fresh=library.contractDefinitions.find(v=>v.id===id&&v.version===version);if(fresh)openContract(fresh);});
  return {load,context,setContext,showContext,sync:contractState,
    async initialize(effectiveOn:string){
      if(value('date-context-on')||!effectiveOn)return;
      const started=fence(),snapshot=await api<Snapshot>('date-schedules/context',{effectiveOn,knownAt:null,mode:'current'});
      if(current(started)&&!value('date-context-on'))showContext(snapshot.context);
    },
    // Unsaved edits: a contract-date definition, or a command whose outcome is unconfirmed; and a date schedule.
    contractsUnsaved:()=>contractDirty||!!pending.command,datesUnsaved:dateEdits.edited,
    clearContext(){pinnedContext=undefined;exactKnownAt=undefined;set('date-context-on','');knownInput.clear();settle();},refresh(){knownInput.refresh();settle();if(shownDates)showDates(shownDates);if(shownCurve)showCurve(shownCurve);},reset(){generation++;pending.clear();contractSelected=undefined;contractDirty=false;get('curve-dates-status').textContent='';library=empty();selected=undefined;form.reset();dateEdits.clear();curveForm.reset();get<HTMLFormElement>('date-expression-form').reset();set('date-context-on','');set('date-context-view','');pinnedContext=undefined;exactKnownAt=undefined;knownInput.clear();settle();options('curve-dates-schedule',[],'Choose a published schedule…');options('curve-dates-example-kind',[],'Choose an example…');clearDates();clearCurve();clearExpression();get('curve-dates-rows').replaceChildren();get('dates-table').replaceChildren();get('curve-dates-table').replaceChildren();list();versionLabel();}};
}
