import {displayDate,displayInstant,publicText} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import {zonedInput} from './user-time.js';

type DateRole='delivery_start'|'delivery_end'|'last_trading';
type Shortcut={name:string;symbol:string;curve:string;position:number;dateRole:DateRole};
type Draft=Shortcut&{at:string};
type Entry={contractId:string;ordinal:number;label:string;eligibleFrom:string;rollAt:string;beforeExpiryAt:string;deliveryFrom:string;deliveryTo:string;lastTradingDate:string};
type Source={curve:string;name:string;from:string;to:string;exampleAt:string;entries:Entry[]};
type Role={code:string;name:string};
type Library={sources:Source[];shortcuts:Shortcut[];dateRoles:Role[]};
type Resolution={symbol:string;sequence:{code:string};position:number;contractId:string;date:string;dateRole:DateRole;valuationAt:string};
type Operations={time:<T>(path:string,body?:unknown)=>Promise<T>;load:()=>Promise<Library>;preview:(draft:Draft)=>Promise<Resolution>;keep:(draft:Draft)=>Promise<Shortcut>;resolve:(body:{symbol:string;at:string})=>Promise<Resolution>};

export function dateRoleName(role:string,roles:Role[]){
  const name=roles.find(r=>r.code===role)?.name;
  if(!name)throw Error('Choose the date that the shortcut returns.');return name;
}
export function resolutionDisplay(result:Resolution,source:Pick<Source,'curve'|'entries'>,roles:Role[]){
  const entry=source.entries.find(e=>e.contractId===result.contractId);
  if(result.sequence.code!==source.curve||!entry)throw Error('The resolved contract does not belong to this source schedule.');
  return {title:`${result.symbol} → ${entry.label}`,date:displayDate(result.date),role:dateRoleName(result.dateRole,roles)};
}
export function scheduleErrorMessage(code:string):string|undefined{
  return ({
    knowledge_coverage:'This knowledge time is outside certified retained history. Choose a covered time or capture current knowledge.',
    knowledge_retention:'Required commit history is unavailable. This cutoff cannot be reconstructed safely.',
    knowledge_tracking:'Commit tracking must be enabled to certify this knowledge time.',
    knowledge_clock:'Clock ordering cannot be verified. Capture is blocked until the temporal history is reviewed.',
    knowledge_timeline:'This database timeline is not the one that certified the retained history.',
    knowledge_limit:'This preview has reached its retained-context limit.',
    temporal_precision:'Knowledge timestamps support microseconds. Finer precision must be supplied explicitly at a representable value.',
    temporal_context:'Choose consistent context coordinates; retained contexts cannot be changed.',
    schedule_context:'Choose an exact context date and a captured publication view.',
    schedule_unpublished:'No published schedule version covers this context and publication view. Publish a version, then explicitly select the current published view.',
    schedule_example:'Choose a supported contract-date example and its explicit published schedule when required.',
    schedule_definition:'Choose an occurrence selection rule and a nonempty effective interval.',
    contract_date_conflict:'This definition or request changed. Reload the saved version or reconcile the original command before editing.',
    schedule_conflict:'This schedule changed or its stable reference already exists. Reload the saved version before editing.',
    schedule_engine:'The retained schedule engine version is unavailable; it cannot be silently reinterpreted.',
    monthly_definition:'Choose the month bounds, anchor and short-month policy explicitly.',
    short_month:'That day is missing in a selected month. Choose another anchor or explicitly allow the last day.',
    calendar_definition:'Supply the calendar name, known coverage, closed weekdays and exact closure dates.',
    calendar_binding:'This date schedule already has a calendar; remove the rolling-reference calendar override.',
    date_coverage:'The requested date is outside the calendar’s known coverage.',
    no_business_day:'The required open day is outside known calendar coverage. Extend the calendar explicitly.',
    roll_convention:'Choose a supported business-day adjustment.',
    date_expression:'Use #name or #name[index], optionally followed by +Nbd or -Nbd. Counts must be positive; omit a zero offset.',
    date_index:'Choose an index within the kept date schedule. Indexes start at 0.',
    date_reference_missing:'Choose a date schedule kept in this demo user’s session.',
    date_definition_conflict:'That name already has different terms. Choose a new shortcut or curve code.',
    contract_dates:'Check contract names, date references and UTC last-trading times. Delivery periods must be ordered and cannot overlap.',
    shortcut_symbol:'Use oe. followed by a lowercase name, for example oe.ng1.',
    shortcut_definition:'Enter a name, shortcut, contract position and the date you want returned.',
    shortcut_conflict:'That shortcut already has different terms. Use a new symbol to keep another definition.',
    shortcut_missing:'Choose a shortcut kept in this demo user’s preview session.',
    sequence_source:'Choose a configured futures schedule. Missing eligibility rules are never inferred.',
    sequence_definition:'This source has incomplete or conflicting contract dates. Its schedule must be corrected before use.',
    sequence_context:'Choose an explicit date and time in your display time zone.',
    sequence_coverage:'Choose a test time within the source schedule’s displayed coverage range.',
    sequence_horizon:'There are not enough remaining contracts for that position at this time.',
    sequence_ineligible:'The expected contract is not yet eligible for trading. A later contract cannot take its place.',
    sequence_date:'The selected contract is missing the requested business date.'
  } as Record<string,string>)[code];
}

export function setupShortcutEditor(run:(work:()=>Promise<void>)=>Promise<void>,operations:Operations,isBusy:()=>boolean,zone:()=>string){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const form=get<HTMLFormElement>('schedule-form');
  const value=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id).value;
  const set=(id:string,text:string)=>{get<HTMLInputElement|HTMLSelectElement>(id).value=text;};
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
  let library:Library={sources:[],shortcuts:[],dateRoles:[]};
  let displayed:Resolution|undefined;
  const timeInput=zonedInput(get<HTMLInputElement>('schedule-at'),zone,operations.time);
  const source=()=>library.sources.find(s=>s.curve===value('schedule-source'));
  function clear(){displayed=undefined;get('schedule-output').hidden=true;get('schedule-summary').textContent='Preview the shortcut to see the contract and date it returns.';}
  form.addEventListener('input',clear);form.addEventListener('change',clear);
  function renderSource(){
    const selected=source();get('schedule-source-panel').hidden=!selected;if(!selected)return;
    get('schedule-coverage').textContent=`Test coverage: ${displayInstant(selected.from,zone())} to ${displayInstant(selected.to,zone())} (end not included). Trading times shown in ${zone()}.`;
    renderRegularTable(get('schedule-contracts'),'schedule-contracts',JSON.stringify(selected),
      ['Order','Contract','Delivery start','Delivery end · not included','Trading eligibility','Last trading'],
      selected.entries.map(e=>[String(e.ordinal),e.label,displayDate(e.deliveryFrom),displayDate(e.deliveryTo),displayInstant(e.eligibleFrom,zone()),displayInstant(e.rollAt,zone())]),[],isBusy);
    const boundary=get<HTMLSelectElement>('schedule-boundary');boundary.replaceChildren(...selected.entries.map(e=>{const o=el('option',e.label);o.value=e.contractId;return o;}));
  }
  function fill(shortcut:Shortcut){
    set('schedule-name',shortcut.name);set('schedule-symbol',shortcut.symbol);set('schedule-source',shortcut.curve);
    set('schedule-position',String(shortcut.position));set('schedule-date-role',shortcut.dateRole);clear();renderSource();
  }
  function renderSaved(){
    get('schedule-saved-empty').hidden=library.shortcuts.length>0;
    get('schedule-saved').replaceChildren(...library.shortcuts.map(shortcut=>{
      const row=el('div');row.className='shortcut-saved-row';
      const description=el('div');description.append(el('strong',shortcut.symbol),el('span',`${shortcut.name} · position ${shortcut.position} · ${dateRoleName(shortcut.dateRole,library.dateRoles)}`));
      const use=el('button','Use shortcut');use.type='button';use.className='secondary';use.onclick=()=>{if(!isBusy())fill(shortcut);};
      row.append(description,use);return row;
    }));
  }
  async function load(){
    const selected=value('schedule-source');library=await operations.load();
    const options=[el('option','Choose a source schedule…'),...library.sources.map(s=>{const o=el('option',s.name);o.value=s.curve;return o;})];
    options[0].value='';get('schedule-source').replaceChildren(...options);set('schedule-source',library.sources.some(s=>s.curve===selected)?selected:'');
    const role=value('schedule-date-role'),first=el('option','Choose a date…');first.value='';
    get('schedule-date-role').replaceChildren(first,...library.dateRoles.map(r=>{const o=el('option',r.name);o.value=r.code;return o;}));set('schedule-date-role',library.dateRoles.some(r=>r.code===role)?role:'');
    get('schedule-unavailable').hidden=library.sources.length>0;renderSource();renderSaved();
  }
  const draft=async():Promise<Draft>=>({name:value('schedule-name'),symbol:value('schedule-symbol'),curve:value('schedule-source'),
    position:Number(value('schedule-position')),dateRole:value('schedule-date-role') as DateRole,at:await timeInput.instant()});
  function render(result:Resolution){
    const selected=source();if(!selected)throw Error('Choose the source schedule.');
    const shown=resolutionDisplay(result,selected,library.dateRoles);
    get('schedule-result-title').textContent=publicText(shown.title);get('schedule-result-date').textContent=shown.date;
    get('schedule-result-detail').textContent=`${shown.role} · contract position ${result.position} · resolved at ${displayInstant(result.valuationAt,zone())}`;
    displayed=result;get('schedule-summary').textContent='Resolved by the shared schedule service.';get('schedule-output').hidden=false;
  }
  form.onsubmit=event=>{event.preventDefault();void run(async()=>{
    clear();const input=await draft();
    const saved=library.shortcuts.some(s=>s.symbol===input.symbol&&s.name===input.name&&s.curve===input.curve&&s.position===input.position&&s.dateRole===input.dateRole);
    render(await(saved?operations.resolve({symbol:input.symbol,at:input.at}):operations.preview(input)));
  });};
  get('schedule-keep').onclick=()=>{
    if(isBusy()||!form.reportValidity())return;
    void run(async()=>{clear();const input=await draft();await operations.keep(input);await load();render(await operations.resolve({symbol:input.symbol,at:input.at}));});
  };
  get('schedule-example').onclick=()=>void run(async()=>{
    if(!library.sources.length)await load();
    // The example uses the source schedule the user chose; none is assumed.
    const example=source();if(!example)throw Error('Choose a source schedule, then load its example.');
    fill({name:`${example.name} prompt`,symbol:'oe.ng1',curve:example.curve,position:1,dateRole:'delivery_start'});timeInput.set(example.exampleAt);get('schedule-context-note').textContent='';
  });
  get('schedule-source').onchange=renderSource;
  for(const event of ['input','change'])get('schedule-at').addEventListener(event,()=>{get('schedule-context-note').textContent='';});
  for(const id of ['schedule-before','schedule-at-boundary'])get(id).onclick=()=>{
    const entry=source()?.entries.find(e=>e.contractId===value('schedule-boundary'));
    if(isBusy()||!entry)return;
    timeInput.set(id==='schedule-before'?entry.beforeExpiryAt:entry.rollAt);
    get('schedule-context-note').textContent=id==='schedule-before'?'Immediately before the recorded expiry instant.':'Using the exact recorded expiry instant.';clear();form.requestSubmit();
  };
  return {load,refresh(){timeInput.refresh();renderSource();if(displayed)render(displayed);},reset(){
    library={sources:[],shortcuts:[],dateRoles:[]};timeInput.clear();form.reset();clear();get('schedule-context-note').textContent='';get('schedule-source-panel').hidden=true;
    const placeholder=el('option','Choose a source schedule…');placeholder.value='';get('schedule-source').replaceChildren(placeholder);
    get('schedule-saved').replaceChildren();get('schedule-contracts').replaceChildren();get('schedule-saved-empty').hidden=false;
  }};
}
